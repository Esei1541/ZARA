using System.Collections.Concurrent;
using Zara.Enforcement.Service.Supervision;

namespace Zara.Enforcement.Service.Tests;

[TestClass]
public sealed class DesktopSupervisorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);
    private static readonly double[] ExpectedCappedBackoffSeconds =
        [1d, 2d, 4d, 8d, 16d, 30d, 30d];
    private static readonly double[] ExpectedResetBackoffSeconds = [1d, 1d];
    private static readonly double[] ExpectedSingleBackoffSecond = [1d];

    [TestMethod]
    public async Task FailedLaunchRetriesForeverWithOneSecondToThirtySecondCappedBackoff()
    {
        var source = CreateRequiredSource();
        var delay = new RecordingImmediateDelay();
        int launchCount = 0;
        var launcher = new DelegateLauncher(_ =>
        {
            int currentCount = Interlocked.Increment(ref launchCount);
            if (currentCount == 8)
            {
                source.PublishNext(
                    restartRequired: false,
                    SupervisionDirectiveReason.ExplicitRelease);
            }

            return ValueTask.FromResult(
                DesktopLaunchResult.RetryableFailure(5, "TestFailure"));
        });
        var supervisor = new DesktopSupervisor(launcher, source, delay);
        using var cancellation = new CancellationTokenSource();

        Task run = supervisor.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => Volatile.Read(ref launchCount) == 8);
        await CancelAndObserveAsync(run, cancellation);

        Assert.AreEqual(8, launchCount);
        CollectionAssert.AreEqual(
            ExpectedCappedBackoffSeconds,
            delay.Delays.Select(item => item.TotalSeconds).ToArray());
        Assert.AreEqual(1, launcher.MaximumConcurrentLaunches);
    }

    [TestMethod]
    public async Task SuccessfulLaunchResetsBackoffBeforeAChildExit()
    {
        var source = CreateRequiredSource();
        var delay = new RecordingImmediateDelay();
        var firstProcess = new FakeSupervisedProcess(processId: 11);
        var secondProcess = new FakeSupervisedProcess(processId: 12);
        int launchCount = 0;
        var launcher = new DelegateLauncher(_ =>
        {
            int current = Interlocked.Increment(ref launchCount);
            return ValueTask.FromResult(current switch
            {
                1 => DesktopLaunchResult.RetryableFailure(5, "FirstFailure"),
                2 => DesktopLaunchResult.Success(firstProcess),
                3 => DesktopLaunchResult.Success(secondProcess),
                _ => throw new InvalidOperationException("Unexpected extra launch."),
            });
        });
        var supervisor = new DesktopSupervisor(launcher, source, delay);
        using var cancellation = new CancellationTokenSource();

        Task run = supervisor.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => Volatile.Read(ref launchCount) >= 2);
        firstProcess.Exit();
        await WaitUntilAsync(() => Volatile.Read(ref launchCount) >= 3);
        source.PublishNext(
            restartRequired: false,
            SupervisionDirectiveReason.ExplicitRelease);
        secondProcess.Exit();
        await WaitUntilAsync(() => secondProcess.IsDisposed);
        await CancelAndObserveAsync(run, cancellation);

        CollectionAssert.AreEqual(
            ExpectedResetBackoffSeconds,
            delay.Delays.Select(item => item.TotalSeconds).ToArray());
        Assert.IsTrue(firstProcess.IsDisposed);
        Assert.IsTrue(secondProcess.IsDisposed);
    }

    [TestMethod]
    public async Task RepeatedRequireCommandsDoNotCreateConcurrentLaunches()
    {
        var source = CreateRequiredSource();
        var launchCompletion = new TaskCompletionSource<DesktopLaunchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeSupervisedProcess(processId: 21);
        var launcher = new DelegateLauncher(_ => new ValueTask<DesktopLaunchResult>(
            launchCompletion.Task));
        var supervisor = new DesktopSupervisor(
            launcher,
            source,
            new RecordingImmediateDelay());
        using var cancellation = new CancellationTokenSource();

        Task run = supervisor.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => launcher.LaunchCount == 1);
        source.PublishNext(
            restartRequired: true,
            SupervisionDirectiveReason.LeaseUpdated);
        source.PublishNext(
            restartRequired: true,
            SupervisionDirectiveReason.LeaseUpdated);

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.AreEqual(1, launcher.LaunchCount);
        Assert.AreEqual(1, launcher.MaximumConcurrentLaunches);

        launchCompletion.SetResult(DesktopLaunchResult.Success(process));
        source.PublishNext(
            restartRequired: false,
            SupervisionDirectiveReason.ExplicitRelease);
        process.Exit();
        await WaitUntilAsync(() => process.IsDisposed);
        await CancelAndObserveAsync(run, cancellation);

        Assert.AreEqual(1, launcher.LaunchCount);
    }

    [TestMethod]
    public async Task ExplicitReleaseInterruptsBackoffAndStopsWithoutAnotherLaunch()
    {
        var source = CreateRequiredSource();
        var delay = new BlockingDelay();
        var launcher = new DelegateLauncher(_ => ValueTask.FromResult(
            DesktopLaunchResult.RetryableFailure(5, "TestFailure")));
        var supervisor = new DesktopSupervisor(launcher, source, delay);
        using var cancellation = new CancellationTokenSource();

        Task run = supervisor.RunAsync(cancellation.Token);
        await delay.Started.Task.WaitAsync(TestTimeout);
        source.PublishNext(
            restartRequired: false,
            SupervisionDirectiveReason.ExplicitRelease);
        await WaitUntilAsync(() => delay.WasCancelled);

        Assert.AreEqual(1, launcher.LaunchCount);
        Assert.IsTrue(delay.WasCancelled);
        Assert.IsFalse(run.IsCompleted);
        await CancelAndObserveAsync(run, cancellation);
    }

    [TestMethod]
    public async Task SessionEndingWaitsForOwnedChildThenStopsWithoutRelaunch()
    {
        var source = CreateRequiredSource();
        var process = new FakeSupervisedProcess(processId: 31);
        var launcher = new DelegateLauncher(_ => ValueTask.FromResult(
            DesktopLaunchResult.Success(process)));
        var supervisor = new DesktopSupervisor(
            launcher,
            source,
            new RecordingImmediateDelay());
        using var cancellation = new CancellationTokenSource();

        Task run = supervisor.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => launcher.LaunchCount == 1);
        source.PublishNext(
            restartRequired: false,
            SupervisionDirectiveReason.SessionEnding);

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.IsFalse(run.IsCompleted);

        process.Exit();
        await WaitUntilAsync(() => process.IsDisposed);

        Assert.AreEqual(1, launcher.LaunchCount);
        Assert.IsTrue(process.IsDisposed);
        Assert.IsFalse(run.IsCompleted);
        await CancelAndObserveAsync(run, cancellation);
    }

    [TestMethod]
    public async Task ReleasedInitialStateWaitsForFutureRequireWithoutLaunchingAtServiceStart()
    {
        var source = new LatestSupervisionCommandSource(new SupervisionDirective(
            Revision: 0,
            RestartRequired: false,
            SupervisionDirectiveReason.ServiceStarted));
        var process = new FakeSupervisedProcess(processId: 35);
        var launcher = new DelegateLauncher(_ => ValueTask.FromResult(
            DesktopLaunchResult.Success(process)));
        var supervisor = new DesktopSupervisor(
            launcher,
            source,
            new RecordingImmediateDelay());
        using var cancellation = new CancellationTokenSource();

        Task run = supervisor.RunAsync(cancellation.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.AreEqual(0, launcher.LaunchCount);
        Assert.IsFalse(run.IsCompleted);

        source.PublishNext(
            restartRequired: true,
            SupervisionDirectiveReason.LeaseUpdated);
        await WaitUntilAsync(() => launcher.LaunchCount == 1);
        source.PublishNext(
            restartRequired: false,
            SupervisionDirectiveReason.ExplicitRelease);
        process.Exit();
        await WaitUntilAsync(() => process.IsDisposed);
        await CancelAndObserveAsync(run, cancellation);
    }

    [TestMethod]
    public async Task UnhealthyGenerationIsDisposedBeforeRetryAndDoesNotBypassBackoff()
    {
        var source = CreateRequiredSource();
        var delay = new RecordingImmediateDelay();
        var unhealthyProcess = new FakeSupervisedProcess(
            processId: 36,
            Task.FromException(new TimeoutException("Health acknowledgement timed out.")));
        var healthyProcess = new FakeSupervisedProcess(processId: 37);
        int launchCount = 0;
        var launcher = new DelegateLauncher(_ =>
        {
            int current = Interlocked.Increment(ref launchCount);
            if (current == 2)
            {
                Assert.IsTrue(unhealthyProcess.IsDisposed);
            }

            return ValueTask.FromResult(current switch
            {
                1 => DesktopLaunchResult.Success(unhealthyProcess),
                2 => DesktopLaunchResult.Success(healthyProcess),
                _ => throw new InvalidOperationException("Unexpected extra launch."),
            });
        });
        var supervisor = new DesktopSupervisor(launcher, source, delay);
        using var cancellation = new CancellationTokenSource();

        Task run = supervisor.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => Volatile.Read(ref launchCount) == 2);
        source.PublishNext(
            restartRequired: false,
            SupervisionDirectiveReason.ExplicitRelease);
        healthyProcess.Exit();
        await WaitUntilAsync(() => healthyProcess.IsDisposed);
        await CancelAndObserveAsync(run, cancellation);

        CollectionAssert.AreEqual(
            ExpectedSingleBackoffSecond,
            delay.Delays.Select(item => item.TotalSeconds).ToArray());
    }

    [TestMethod]
    public async Task LaunchHandshakeIsSessionBoundAndConsumedOnceAfterHealth()
    {
        var registry = new DesktopLaunchHandshakeRegistry();
        IDesktopLaunchHandshake handshake = await registry.CreateAsync(
            sessionId: 5,
            CancellationToken.None);

        Assert.AreEqual(64, handshake.OneTimeToken.Length);
        Assert.IsTrue(handshake.TryBindProcess(processId: 100));
        Assert.IsFalse(handshake.TryBindProcess(processId: 101));
        Assert.IsFalse(registry.TryReportHealthy(
            handshake.OneTimeToken,
            sessionId: 6,
            processId: 100));
        Assert.IsFalse(registry.TryReportHealthy(
            handshake.OneTimeToken,
            sessionId: 5,
            processId: 101));
        Assert.IsTrue(registry.TryReportHealthy(
            handshake.OneTimeToken,
            sessionId: 5,
            processId: 100));
        await handshake.WaitForHealthyAsync(CancellationToken.None);
        Assert.IsFalse(registry.TryReportHealthy(
            handshake.OneTimeToken,
            sessionId: 5,
            processId: 100));

        await handshake.DisposeAsync();
    }

    [TestMethod]
    public async Task ServiceCancellationDisposesExactOwnedProcess()
    {
        var source = CreateRequiredSource();
        var process = new FakeSupervisedProcess(processId: 41);
        var launcher = new DelegateLauncher(_ => ValueTask.FromResult(
            DesktopLaunchResult.Success(process)));
        var supervisor = new DesktopSupervisor(
            launcher,
            source,
            new RecordingImmediateDelay());
        using var cancellation = new CancellationTokenSource();

        Task run = supervisor.RunAsync(cancellation.Token);
        await WaitUntilAsync(() => launcher.LaunchCount == 1);
        cancellation.Cancel();

        try
        {
            await run.WaitAsync(TestTimeout);
            Assert.Fail("Cancellation should end the supervisor task.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.IsTrue(process.IsDisposed);
    }

    [TestMethod]
    public async Task LatestCommandSourceCoalescesNewerRevisionsAndRejectsStaleOnes()
    {
        var source = CreateRequiredSource();

        Assert.IsTrue(source.TryPublish(new SupervisionDirective(
            Revision: 2,
            RestartRequired: true,
            SupervisionDirectiveReason.LeaseUpdated)));
        Assert.IsFalse(source.TryPublish(new SupervisionDirective(
            Revision: 1,
            RestartRequired: false,
            SupervisionDirectiveReason.ExplicitRelease)));

        SupervisionDirective observed = await source.WaitForChangeAsync(
            observedRevision: 0,
            CancellationToken.None);

        Assert.AreEqual(2, observed.Revision);
        Assert.IsTrue(observed.RestartRequired);
    }

    private static LatestSupervisionCommandSource CreateRequiredSource()
    {
        return new(new SupervisionDirective(
            Revision: 0,
            RestartRequired: true,
            SupervisionDirectiveReason.ServiceStarted));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static async Task CancelAndObserveAsync(
        Task run,
        CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        try
        {
            await run.WaitAsync(TestTimeout);
            Assert.Fail("Service cancellation should end the supervisor task.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class DelegateLauncher : IDesktopProcessLauncher
    {
        private readonly Func<CancellationToken, ValueTask<DesktopLaunchResult>> _launch;
        private int _activeLaunches;
        private int _launchCount;
        private int _maximumConcurrentLaunches;

        public DelegateLauncher(
            Func<CancellationToken, ValueTask<DesktopLaunchResult>> launch)
        {
            _launch = launch;
        }

        public int LaunchCount => Volatile.Read(ref _launchCount);

        public int MaximumConcurrentLaunches => Volatile.Read(
            ref _maximumConcurrentLaunches);

        public async ValueTask<DesktopLaunchResult> LaunchAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _launchCount);
            int active = Interlocked.Increment(ref _activeLaunches);
            SetMaximum(ref _maximumConcurrentLaunches, active);
            try
            {
                return await _launch(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeLaunches);
            }
        }

        private static void SetMaximum(ref int target, int candidate)
        {
            int current;
            do
            {
                current = Volatile.Read(ref target);
                if (candidate <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, candidate, current) != current);
        }
    }

    private sealed class FakeSupervisedProcess : ISupervisedProcess
    {
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Task _ready;

        public FakeSupervisedProcess(int processId, Task? ready = null)
        {
            ProcessId = processId;
            _ready = ready ?? Task.CompletedTask;
        }

        public int ProcessId { get; }

        public int SessionId => 1;

        public bool IsDisposed { get; private set; }

        public void Exit()
        {
            _exited.TrySetResult();
        }

        public Task WaitForReadyAsync(CancellationToken cancellationToken)
        {
            return _ready.WaitAsync(cancellationToken);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return _exited.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingImmediateDelay : ISupervisionDelay
    {
        private readonly ConcurrentQueue<TimeSpan> _delays = new();

        public IReadOnlyCollection<TimeSpan> Delays => _delays.ToArray();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _delays.Enqueue(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDelay : ISupervisionDelay
    {
        private int _wasCancelled;

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled => Volatile.Read(ref _wasCancelled) != 0;

        public async Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(1), delay);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _wasCancelled, 1);
                throw;
            }
        }
    }
}
