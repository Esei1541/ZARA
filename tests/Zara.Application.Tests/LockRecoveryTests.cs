using System.Collections.Concurrent;
using System.Threading.Channels;
using Zara.Application.Locking;
using Zara.Core.Runtime;

namespace Zara.Application.Tests;

[TestClass]
public sealed class LockRecoveryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task LockRequestsBeforeOneMinuteKeepSuccessfulOverlayAndDoNotRetryInput()
    {
        var clock = new RecoveryTestClock();
        var delays = new ControlledRecoveryDelay();
        var overlay = new RecoveryOverlayPort();
        var input = new RecoveryInputPort { FailuresRemaining = 1 };
        using var runtime = new LockRuntimeUseCase(overlay, input, clock, delays.WaitAsync);
        var recovered = ObserveRecoveryEnd(runtime);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RequestLockAsync());
        PendingRecoveryDelay pending = await delays.NextAsync();
        Assert.AreEqual(TimeSpan.FromMinutes(1), pending.Duration);
        await runtime.RequestLockAsync();
        clock.Advance(TimeSpan.FromSeconds(59));
        await runtime.RequestLockAsync();
        Assert.AreEqual(1, overlay.Shows);
        Assert.AreEqual(1, input.Enables);
        Assert.AreEqual(0, input.Disables);
        Assert.IsTrue(runtime.CurrentRecovery.IsRecovering);

        clock.Advance(TimeSpan.FromSeconds(1));
        pending.Complete();
        await recovered.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(1, overlay.Shows);
        Assert.AreEqual(2, input.Enables);
        Assert.AreEqual(0, input.Disables);
        Assert.AreEqual(OverlayProjectionState.Visible, runtime.CurrentState.OverlayProjection);
    }

    [TestMethod]
    public async Task RepeatedFailuresRetryEveryMinuteAndNotifyEpisodeStartAndEndOnlyOnce()
    {
        var clock = new RecoveryTestClock();
        var delays = new ControlledRecoveryDelay();
        var overlay = new RecoveryOverlayPort();
        var input = new RecoveryInputPort { FailuresRemaining = 2 };
        using var runtime = new LockRuntimeUseCase(overlay, input, clock, delays.WaitAsync);
        var notifications = new ConcurrentQueue<LockRecoveryState>();
        runtime.RecoveryStateChanged += (_, state) => notifications.Enqueue(state);
        var recovered = ObserveRecoveryEnd(runtime);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RequestLockAsync());
        PendingRecoveryDelay firstDelay = await delays.NextAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        firstDelay.Complete();
        PendingRecoveryDelay secondDelay = await delays.NextAsync();

        Assert.AreEqual(TimeSpan.FromMinutes(1), secondDelay.Duration);
        Assert.AreEqual(2, input.Enables);
        Assert.AreEqual(1, overlay.Shows);
        Assert.AreEqual(0, overlay.Hides);
        Assert.AreEqual(0, input.Disables);
        CollectionAssert.AreEqual(new[] { new LockRecoveryState(true, 1) }, notifications.ToArray());

        clock.Advance(TimeSpan.FromSeconds(59));
        await runtime.RequestLockAsync();
        Assert.AreEqual(2, input.Enables);
        clock.Advance(TimeSpan.FromSeconds(1));
        secondDelay.Complete();
        await recovered.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(3, input.Enables);
        Assert.AreEqual(1, overlay.Shows);
        CollectionAssert.AreEqual(
            new[] { new LockRecoveryState(true, 1), new LockRecoveryState(false, 1) },
            notifications.ToArray());
        Assert.AreEqual(new LockRecoveryState(false, 1), runtime.CurrentRecovery);
    }

    [TestMethod]
    public async Task InitialOverlayFailureWaitsOneMinuteBeforeShowingAndEnablingInput()
    {
        var clock = new RecoveryTestClock();
        var delays = new ControlledRecoveryDelay();
        var overlay = new RecoveryOverlayPort { FailuresRemaining = 1 };
        var input = new RecoveryInputPort();
        using var runtime = new LockRuntimeUseCase(overlay, input, clock, delays.WaitAsync);
        var recovered = ObserveRecoveryEnd(runtime);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RequestLockAsync());
        PendingRecoveryDelay pending = await delays.NextAsync();
        Assert.AreEqual(0, input.Enables);
        Assert.AreEqual(0, overlay.Hides);

        clock.Advance(TimeSpan.FromMinutes(1));
        pending.Complete();
        await recovered.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(2, overlay.Shows);
        Assert.AreEqual(1, input.Enables);
        Assert.AreEqual(OverlayProjectionState.Visible, runtime.CurrentState.OverlayProjection);
    }

    [TestMethod]
    public async Task TopologyInvalidationRetriesOverlayAndPreservesSuccessfulInput()
    {
        var clock = new RecoveryTestClock();
        var delays = new ControlledRecoveryDelay();
        var overlay = new RecoveryOverlayPort();
        var input = new RecoveryInputPort();
        using var runtime = new LockRuntimeUseCase(overlay, input, clock, delays.WaitAsync);
        await runtime.RequestLockAsync();
        var recovered = ObserveRecoveryEnd(runtime);
        long revision = runtime.CurrentIntent.Revision;

        await runtime.ReportOverlayProjectionInvalidatedAsync(OverlayVisibility.Visible);
        PendingRecoveryDelay pending = await delays.NextAsync();
        Assert.AreEqual(OverlayProjectionState.Unknown, runtime.CurrentState.OverlayProjection);
        Assert.AreEqual(1, overlay.Shows);
        Assert.AreEqual(revision, runtime.CurrentIntent.Revision);

        clock.Advance(TimeSpan.FromMinutes(1));
        pending.Complete();
        await recovered.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(2, overlay.Shows);
        Assert.AreEqual(1, input.Enables);
        Assert.AreEqual(0, input.Disables);
        Assert.AreEqual(revision, runtime.CurrentIntent.Revision);
    }

    [TestMethod]
    public async Task RepeatedTopologyInvalidationDoesNotPostponeTheScheduledRetry()
    {
        var clock = new RecoveryTestClock();
        var delays = new ControlledRecoveryDelay();
        var overlay = new RecoveryOverlayPort();
        var input = new RecoveryInputPort();
        using var runtime = new LockRuntimeUseCase(overlay, input, clock, delays.WaitAsync);
        var notifications = new ConcurrentQueue<LockRecoveryState>();
        runtime.RecoveryStateChanged += (_, state) => notifications.Enqueue(state);
        var recovered = ObserveRecoveryEnd(runtime);
        await runtime.RequestLockAsync();

        await runtime.ReportOverlayProjectionInvalidatedAsync(OverlayVisibility.Visible);
        PendingRecoveryDelay pending = await delays.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await runtime.ReportOverlayProjectionInvalidatedAsync(OverlayVisibility.Visible);
        await runtime.RequestLockAsync();
        Assert.AreEqual(1, overlay.Shows);

        clock.Advance(TimeSpan.FromSeconds(30));
        pending.Complete();
        await recovered.Task.WaitAsync(TestTimeout);

        Assert.AreEqual(2, overlay.Shows);
        Assert.AreEqual(1, input.Enables);
        CollectionAssert.AreEqual(
            new[] { new LockRecoveryState(true, 1), new LockRecoveryState(false, 1) },
            notifications.ToArray());
    }

    [TestMethod]
    [DataRow("regular")]
#if DEBUG
    [DataRow("development")]
#endif
    [DataRow("exit")]
    [DataRow("dispose")]
    public async Task UnlockExitAndDisposalCancelPendingRetryWithoutRelocking(string route)
    {
        var clock = new RecoveryTestClock();
        var delays = new ControlledRecoveryDelay();
        var overlay = new RecoveryOverlayPort();
        var input = new RecoveryInputPort { FailuresRemaining = 1 };
        using var runtime = new LockRuntimeUseCase(overlay, input, clock, delays.WaitAsync);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RequestLockAsync());
        PendingRecoveryDelay pending = await delays.NextAsync();

        switch (route)
        {
            case "regular":
                await runtime.RequestUnlockAsync();
                break;
#if DEBUG
            case "development":
                await runtime.RequestDevelopmentUnlockAsync();
                break;
#endif

            case "exit":
                await runtime.PrepareForExitAsync();
                break;
            case "dispose":
                runtime.Dispose();
                break;
        }

        await pending.Exited.Task.WaitAsync(TestTimeout);
        Assert.IsTrue(pending.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMinutes(2));
        pending.Complete();
        Assert.AreEqual(1, overlay.Shows);
        Assert.AreEqual(1, input.Enables);
        if (route != "dispose")
        {
            Assert.AreEqual(1, input.Disables);
            Assert.AreEqual(1, overlay.Hides);
            Assert.IsFalse(runtime.CurrentRecovery.IsRecovering);
            Assert.AreEqual(LockState.Unlocked, runtime.CurrentIntent.DesiredLock);
        }
    }

    [TestMethod]
    public async Task NewFailureAfterRecoveryStartsANewNotificationEpisode()
    {
        var clock = new RecoveryTestClock();
        var delays = new ControlledRecoveryDelay();
        var overlay = new RecoveryOverlayPort { FailuresRemaining = 1 };
        using var runtime = new LockRuntimeUseCase(overlay, new RecoveryInputPort(), clock, delays.WaitAsync);
        var notifications = new ConcurrentQueue<LockRecoveryState>();
        runtime.RecoveryStateChanged += (_, state) => notifications.Enqueue(state);
        var firstRecovery = ObserveRecoveryEnd(runtime);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RequestLockAsync());
        PendingRecoveryDelay firstDelay = await delays.NextAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        firstDelay.Complete();
        await firstRecovery.Task.WaitAsync(TestTimeout);

        var secondRecovery = ObserveRecoveryEnd(runtime);
        await runtime.ReportOverlayProjectionInvalidatedAsync(OverlayVisibility.Visible);
        PendingRecoveryDelay secondDelay = await delays.NextAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        secondDelay.Complete();
        await secondRecovery.Task.WaitAsync(TestTimeout);

        CollectionAssert.AreEqual(
            new[]
            {
                new LockRecoveryState(true, 1), new LockRecoveryState(false, 1),
                new LockRecoveryState(true, 2), new LockRecoveryState(false, 2),
            },
            notifications.ToArray());
    }

    private static TaskCompletionSource ObserveRecoveryEnd(LockRuntimeUseCase runtime)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.RecoveryStateChanged += (_, state) =>
        {
            if (!state.IsRecovering)
            {
                completion.TrySetResult();
            }
        };
        return completion;
    }

    private sealed class ControlledRecoveryDelay
    {
        private readonly Channel<PendingRecoveryDelay> _pending = Channel.CreateUnbounded<PendingRecoveryDelay>();

        internal async Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            var pending = new PendingRecoveryDelay(duration, cancellationToken);
            if (!_pending.Writer.TryWrite(pending))
            {
                throw new InvalidOperationException("Could not publish a pending test delay.");
            }

            try
            {
                await pending.Completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                pending.Exited.TrySetResult();
            }
        }

        internal Task<PendingRecoveryDelay> NextAsync() =>
            _pending.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
    }

    private sealed class PendingRecoveryDelay(TimeSpan duration, CancellationToken token)
    {
        internal TimeSpan Duration { get; } = duration;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete() => Completion.TrySetResult();
    }

    private sealed class RecoveryOverlayPort : ILockOverlayPort
    {
        private int _shows;
        private int _hides;
        internal int FailuresRemaining { get; set; }
        internal int Shows => Volatile.Read(ref _shows);
        internal int Hides => Volatile.Read(ref _hides);

        public Task ShowAllAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _shows);
            return FailuresRemaining-- > 0
                ? Task.FromException(new InvalidOperationException("Overlay failed."))
                : Task.CompletedTask;
        }

        public Task HideAllAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _hides);
            return Task.CompletedTask;
        }
    }

    private sealed class RecoveryInputPort : ILockInputPort
    {
        private int _enables;
        private int _disables;
        internal int FailuresRemaining { get; set; }
        internal int Enables => Volatile.Read(ref _enables);
        internal int Disables => Volatile.Read(ref _disables);

        public Task EnableAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _enables);
            return FailuresRemaining-- > 0
                ? Task.FromException(new InvalidOperationException("Input failed."))
                : Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _disables);
            return Task.CompletedTask;
        }
    }
}

internal sealed class RecoveryTestClock : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
    internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
}
