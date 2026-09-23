using Zara.Application.Startup;

namespace Zara.Application.Tests;

[TestClass]
public sealed class DesktopStartupUseCaseTests
{
    private static readonly bool[] NormalStart = [false];
    private static readonly bool[] ElevatedRetry = [false, true];

    [TestMethod]
    public async Task RunningServiceActivatesExistingDesktopWithoutConnectingOrStarting()
    {
        var service = new ServicePort { Status = new(StartupServiceState.Running) };
        var desktop = new DesktopPort { Activate = (_, _) => Task.FromResult(true) };

        DesktopStartupOutcome outcome = await Create(service, desktop).PrepareAsync(CancellationToken.None);

        Assert.AreEqual(DesktopStartupOutcome.ActivatedExisting, outcome);
        Assert.AreEqual(1, desktop.ActivationCount);
        Assert.AreEqual(0, desktop.ConnectionCount);
        Assert.IsEmpty(service.StartCalls);
    }

    [TestMethod]
    public async Task RunningServiceWithoutDesktopConnectsManualLaunch()
    {
        var service = new ServicePort { Status = new(StartupServiceState.Running) };
        var desktop = new DesktopPort();

        DesktopStartupOutcome outcome = await Create(service, desktop).PrepareAsync(CancellationToken.None);

        Assert.AreEqual(DesktopStartupOutcome.ReadyHere, outcome);
        Assert.AreEqual(1, desktop.ConnectionCount);
        Assert.IsEmpty(service.StartCalls);
    }

    [TestMethod]
    public async Task StoppedServiceStartsOnceAndWaitsForServiceDesktopActivation()
    {
        var service = new ServicePort
        {
            Read = count => new(count == 1 ? StartupServiceState.Stopped : StartupServiceState.Running),
        };
        var desktop = new DesktopPort
        {
            Activate = (count, _) => Task.FromResult(count == 3),
        };

        DesktopStartupOutcome outcome = await Create(service, desktop).PrepareAsync(CancellationToken.None);

        Assert.AreEqual(DesktopStartupOutcome.ActivatedExisting, outcome);
        CollectionAssert.AreEqual(NormalStart, service.StartCalls);
        Assert.AreEqual(3, desktop.ActivationCount);
        Assert.AreEqual(0, desktop.ConnectionCount);
    }

    [TestMethod]
    public async Task StartPendingWaitsForServiceOwnedDesktopWithoutStartingOrConnecting()
    {
        var service = new ServicePort
        {
            Read = count => new(count < 3 ? StartupServiceState.StartPending : StartupServiceState.Running),
        };
        var desktop = new DesktopPort
        {
            Activate = (count, _) => Task.FromResult(count == 2),
        };

        DesktopStartupOutcome outcome = await Create(service, desktop).PrepareAsync(CancellationToken.None);

        Assert.AreEqual(DesktopStartupOutcome.ActivatedExisting, outcome);
        Assert.IsEmpty(service.StartCalls);
        Assert.AreEqual(0, desktop.ConnectionCount);
    }

    [TestMethod]
    public async Task StopPendingWaitsUntilStoppedBeforeStartingOnce()
    {
        var service = new ServicePort
        {
            Read = count => new(count < 3 ? StartupServiceState.StopPending :
                count == 3 ? StartupServiceState.Stopped : StartupServiceState.Running),
        };
        var desktop = new DesktopPort { Activate = (_, _) => Task.FromResult(true) };

        DesktopStartupOutcome outcome = await Create(service, desktop).PrepareAsync(CancellationToken.None);

        Assert.AreEqual(DesktopStartupOutcome.ActivatedExisting, outcome);
        Assert.AreEqual(4, service.StatusReadCount);
        CollectionAssert.AreEqual(NormalStart, service.StartCalls);
    }

    [TestMethod]
    public async Task ServiceStoppingAfterStartFailsWithoutRestarting()
    {
        var service = new ServicePort
        {
            Read = count => count == 1
                ? new(StartupServiceState.Stopped)
                : new(StartupServiceState.Stopped, Win32ExitCode: 1067, ServiceExitCode: 42),
        };
        var desktop = new DesktopPort();

        DesktopStartupException error = await Assert.ThrowsExactlyAsync<DesktopStartupException>(
            () => Create(service, desktop).PrepareAsync(CancellationToken.None));

        Assert.AreEqual(StartupFailureKind.ServiceStopped, error.Kind);
        Assert.AreEqual(1067, error.NativeErrorCode);
        Assert.AreEqual((uint)42, error.ServiceExitCode);
        CollectionAssert.AreEqual(NormalStart, service.StartCalls);
        Assert.AreEqual(0, desktop.ConnectionCount);
    }

    [TestMethod]
    public async Task AccessDeniedPromptsOnceThenRetriesStartElevated()
    {
        var service = new ServicePort
        {
            Read = count => new(count == 1 ? StartupServiceState.Stopped : StartupServiceState.Running),
            Start = (elevated, _) => elevated
                ? Task.CompletedTask
                : Task.FromException(new DesktopStartupException(StartupFailureKind.AccessDenied, "denied")),
        };
        var desktop = new DesktopPort { Activate = (_, _) => Task.FromResult(true) };
        int prompts = 0;

        DesktopStartupOutcome outcome = await Create(service, desktop, _ =>
        {
            prompts++;
            return Task.FromResult(true);
        }).PrepareAsync(CancellationToken.None);

        Assert.AreEqual(DesktopStartupOutcome.ActivatedExisting, outcome);
        Assert.AreEqual(1, prompts);
        CollectionAssert.AreEqual(ElevatedRetry, service.StartCalls);
    }

    [TestMethod]
    public async Task CancelledElevationDoesNotAttemptStartAgain()
    {
        var service = new ServicePort
        {
            Status = new(StartupServiceState.Stopped),
            Start = (_, _) => Task.FromException(
                new DesktopStartupException(StartupFailureKind.AccessDenied, "denied")),
        };
        int prompts = 0;

        DesktopStartupException error = await Assert.ThrowsExactlyAsync<DesktopStartupException>(
            () => Create(service, new DesktopPort(), _ =>
            {
                prompts++;
                return Task.FromResult(false);
            }).PrepareAsync(CancellationToken.None));

        Assert.AreEqual(StartupFailureKind.ElevationCancelled, error.Kind);
        Assert.AreEqual(1, prompts);
        CollectionAssert.AreEqual(NormalStart, service.StartCalls);
    }

    [TestMethod]
    public async Task DisabledServiceFailsBeforeStartOrConsent()
    {
        var service = new ServicePort
        {
            Status = new(StartupServiceState.Stopped, StartDisabled: true),
        };
        int prompts = 0;

        DesktopStartupException error = await Assert.ThrowsExactlyAsync<DesktopStartupException>(
            () => Create(service, new DesktopPort(), _ =>
            {
                prompts++;
                return Task.FromResult(true);
            }).PrepareAsync(CancellationToken.None));

        Assert.AreEqual(StartupFailureKind.ServiceDisabled, error.Kind);
        Assert.IsEmpty(service.StartCalls);
        Assert.AreEqual(0, prompts);
    }

    [TestMethod]
    [DataRow(StartupFailureKind.ServiceMissing)]
    [DataRow(StartupFailureKind.BinaryMissing)]
    [DataRow(StartupFailureKind.IdentityMismatch)]
    public async Task PermanentStartFailureDoesNotPromptOrRetry(StartupFailureKind kind)
    {
        var service = new ServicePort
        {
            Status = new(StartupServiceState.Stopped),
            Start = (_, _) => Task.FromException(new DesktopStartupException(kind, "start failed")),
        };
        int prompts = 0;

        DesktopStartupException error = await Assert.ThrowsExactlyAsync<DesktopStartupException>(
            () => Create(service, new DesktopPort(), _ =>
            {
                prompts++;
                return Task.FromResult(true);
            }).PrepareAsync(CancellationToken.None));

        Assert.AreEqual(kind, error.Kind);
        CollectionAssert.AreEqual(NormalStart, service.StartCalls);
        Assert.AreEqual(0, prompts);
    }

    [TestMethod]
    public async Task TransientConnectionFailuresRetryWithinPreparationBudget()
    {
        var service = new ServicePort { Status = new(StartupServiceState.Running) };
        var desktop = new DesktopPort
        {
            Connect = (count, _) => count < 3
                ? Task.FromException<DesktopStartupOutcome>(new DesktopStartupException(
                    count == 1 ? StartupFailureKind.ConnectionFailed : StartupFailureKind.TimedOut,
                    "transient"))
                : Task.FromResult(DesktopStartupOutcome.ReadyHere),
        };

        DesktopStartupOutcome outcome = await Create(service, desktop).PrepareAsync(CancellationToken.None);

        Assert.AreEqual(DesktopStartupOutcome.ReadyHere, outcome);
        Assert.AreEqual(3, desktop.ConnectionCount);
        Assert.IsEmpty(service.StartCalls);
    }

    [TestMethod]
    public async Task WaitForServiceDesktopNeverReconnectsAndOnlyActivates()
    {
        var service = new ServicePort { Status = new(StartupServiceState.Running) };
        var desktop = new DesktopPort
        {
            Activate = (count, _) => Task.FromResult(count == 3),
            Connect = (_, _) => Task.FromResult(DesktopStartupOutcome.WaitForServiceDesktop),
        };

        DesktopStartupOutcome outcome = await Create(service, desktop).PrepareAsync(CancellationToken.None);

        Assert.AreEqual(DesktopStartupOutcome.ActivatedExisting, outcome);
        Assert.AreEqual(3, desktop.ActivationCount);
        Assert.AreEqual(1, desktop.ConnectionCount);
    }

    [TestMethod]
    public async Task ServiceStartTimeDoesNotConsumeDesktopPreparationBudget()
    {
        var time = new ManualTimeProvider();
        var service = new ServicePort
        {
            Read = count => new(count == 1 ? StartupServiceState.Stopped : StartupServiceState.Running),
            Start = (_, _) =>
            {
                time.Advance(TimeSpan.FromSeconds(45));
                return Task.CompletedTask;
            },
        };
        var desktop = new DesktopPort { Activate = (_, _) => Task.FromResult(true) };

        DesktopStartupOutcome outcome = await Create(service, desktop, time: time).PrepareAsync(CancellationToken.None);

        Assert.AreEqual(DesktopStartupOutcome.ActivatedExisting, outcome);
        CollectionAssert.AreEqual(NormalStart, service.StartCalls);
    }

    [TestMethod]
    public async Task StartPendingTimesOutAtThirtySecondsWithoutStarting()
    {
        var time = new ManualTimeProvider();
        var service = new ServicePort { Status = new(StartupServiceState.StartPending) };

        DesktopStartupException error = await Assert.ThrowsExactlyAsync<DesktopStartupException>(
            () => Create(service, new DesktopPort(), time: time).PrepareAsync(CancellationToken.None));

        Assert.AreEqual(StartupFailureKind.TimedOut, error.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(30), time.Elapsed);
        Assert.IsEmpty(service.StartCalls);
    }

    [TestMethod]
    public async Task ConnectionFailureAtThirtySecondBudgetReportsTimeout()
    {
        var time = new ManualTimeProvider();
        var service = new ServicePort { Status = new(StartupServiceState.Running) };
        var desktop = new DesktopPort
        {
            Connect = (_, _) => Task.FromException<DesktopStartupOutcome>(
                new DesktopStartupException(StartupFailureKind.ConnectionFailed, "transient")),
        };

        DesktopStartupException error = await Assert.ThrowsExactlyAsync<DesktopStartupException>(
            () => Create(service, desktop, time: time).PrepareAsync(CancellationToken.None));

        Assert.AreEqual(StartupFailureKind.TimedOut, error.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(30), time.Elapsed);
        Assert.IsGreaterThan(1, desktop.ConnectionCount);
    }

    [TestMethod]
    public async Task CancellationStopsPollingWithoutStartingOrConnecting()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new ServicePort { Status = new(StartupServiceState.StartPending) };
        var time = new ManualTimeProvider();
        DesktopStartupUseCase useCase = Create(service, new DesktopPort(), time: time,
            delay: (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            });

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => useCase.PrepareAsync(cancellation.Token));

        Assert.AreEqual(1, service.StatusReadCount);
        Assert.IsEmpty(service.StartCalls);
    }

    private static DesktopStartupUseCase Create(
        ServicePort service,
        DesktopPort desktop,
        Func<CancellationToken, Task<bool>>? confirm = null,
        ManualTimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        time ??= new ManualTimeProvider();
        return new DesktopStartupUseCase(
            service,
            desktop,
            confirm ?? (_ => Task.FromResult(false)),
            time,
            delay ?? ((duration, token) =>
            {
                token.ThrowIfCancellationRequested();
                time.Advance(duration);
                return Task.CompletedTask;
            }));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public TimeSpan Elapsed => TimeSpan.FromTicks(_timestamp);

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class ServicePort : IServiceStartupPort
    {
        public ServiceStartupStatus Status { get; set; } = new(StartupServiceState.Running);

        public Func<int, ServiceStartupStatus>? Read { get; set; }

        public Func<bool, CancellationToken, Task>? Start { get; set; }

        public int StatusReadCount { get; private set; }

        public List<bool> StartCalls { get; } = [];

        public ServiceStartupStatus ReadStatus()
        {
            StatusReadCount++;
            return Read?.Invoke(StatusReadCount) ?? Status;
        }

        public Task StartAsync(bool elevated, CancellationToken cancellationToken)
        {
            StartCalls.Add(elevated);
            return Start?.Invoke(elevated, cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class DesktopPort : IDesktopStartupPort
    {
        public Func<int, CancellationToken, Task<bool>>? Activate { get; set; }

        public Func<int, CancellationToken, Task<DesktopStartupOutcome>>? Connect { get; set; }

        public int ActivationCount { get; private set; }

        public int ConnectionCount { get; private set; }

        public Task<bool> TryActivateAsync(CancellationToken cancellationToken)
        {
            ActivationCount++;
            return Activate?.Invoke(ActivationCount, cancellationToken) ?? Task.FromResult(false);
        }

        public Task<DesktopStartupOutcome> ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectionCount++;
            return Connect?.Invoke(ConnectionCount, cancellationToken) ??
                Task.FromResult(DesktopStartupOutcome.ReadyHere);
        }
    }
}
