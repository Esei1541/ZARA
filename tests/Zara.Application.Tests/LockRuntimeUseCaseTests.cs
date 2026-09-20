using System.Collections.Concurrent;
using Zara.Application.Locking;
using Zara.Core.Runtime;

namespace Zara.Application.Tests;

[TestClass]
public sealed class LockRuntimeUseCaseTests
{
    private static readonly string[] ExpectedLockAndUnlockCalls = ["Show", "Enable", "Disable", "Hide"];
    private static readonly string[] ExpectedEnableThenDisableCalls = ["Enable", "Disable"];
    private static readonly string[] ExpectedRelockInputCalls = ["Enable", "Disable", "Enable"];
    private static readonly string[] ExpectedShowThenHideCalls = ["Show", "Hide"];

    [TestMethod]
    public void ConstructorWithNullOverlayPortThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new LockRuntimeUseCase(null!, new RecordingInputPort()));
    }

    [TestMethod]
    public void CurrentStateBeforeRequestsIsInitialState()
    {
        using var useCase = new LockRuntimeUseCase(new RecordingOverlayPort(), new RecordingInputPort());

        Assert.AreEqual(RuntimeState.Initial, useCase.CurrentState);
        Assert.AreEqual(
            new LockIntentSnapshot(LockState.Unlocked, Revision: 0),
            useCase.CurrentIntent);
    }

    [TestMethod]
    public async Task ExplicitIntentRequestsAdvanceRevisionEvenWhenProjectionIsAlreadySatisfied()
    {
        using var useCase = new LockRuntimeUseCase(new RecordingOverlayPort(), new RecordingInputPort());

        await useCase.RequestLockAsync();
        await useCase.RequestLockAsync();
        await useCase.RequestUnlockAsync();

        Assert.AreEqual(
            new LockIntentSnapshot(LockState.Unlocked, Revision: 3),
            useCase.CurrentIntent);
    }

    [TestMethod]
    public async Task FailedProjectionStillAdvancesRequestedIntentRevision()
    {
        var port = new RecordingOverlayPort
        {
            ShowOperation = _ => Task.FromException(new InvalidOperationException("Show failed.")),
        };
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.RequestLockAsync());

        Assert.AreEqual(
            new LockIntentSnapshot(LockState.Locked, Revision: 1),
            useCase.CurrentIntent);
    }

    [TestMethod]
    public async Task RestoreLockIfIntentRevisionAsyncWhenRevisionMatchesRestoresAndAdvancesIntent()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());
        await useCase.RequestLockAsync();
        await useCase.RequestUnlockAsync();

        bool restored = await useCase.RestoreLockIfIntentRevisionAsync(expectedIntentRevision: 2);

        Assert.IsTrue(restored);
        Assert.AreEqual(
            new LockIntentSnapshot(LockState.Locked, Revision: 3),
            useCase.CurrentIntent);
        Assert.AreEqual(2, port.ShowCallCount);
    }

    [TestMethod]
    public async Task RestoreLockIfIntentRevisionAsyncWhenRevisionIsStaleDoesNotChangeIntent()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());
        await useCase.RequestLockAsync();
        await useCase.RequestUnlockAsync();

        bool restored = await useCase.RestoreLockIfIntentRevisionAsync(expectedIntentRevision: 1);

        Assert.IsFalse(restored);
        Assert.AreEqual(
            new LockIntentSnapshot(LockState.Unlocked, Revision: 2),
            useCase.CurrentIntent);
        Assert.AreEqual(1, port.ShowCallCount);
        Assert.AreEqual(1, port.HideCallCount);
    }

    [TestMethod]
    public async Task ProjectionInvalidationDoesNotAdvanceIntentRevision()
    {
        using var useCase = new LockRuntimeUseCase(new RecordingOverlayPort(), new RecordingInputPort());
        await useCase.RequestLockAsync();

        await useCase.ReportOverlayProjectionInvalidatedAsync(OverlayVisibility.Visible);

        Assert.AreEqual(
            new LockIntentSnapshot(LockState.Locked, Revision: 1),
            useCase.CurrentIntent);
    }

    [TestMethod]
    public async Task RequestLockAsyncShowsOverlayAndConfirmsVisibleState()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());

        await useCase.RequestLockAsync();

        Assert.AreEqual(1, port.ShowCallCount);
        Assert.AreEqual(0, port.HideCallCount);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Visible);
    }

    [TestMethod]
    public async Task RequestLockAsyncWhenRepeatedSequentiallyShowsOnlyOnce()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());

        await useCase.RequestLockAsync();
        await useCase.RequestLockAsync();

        Assert.AreEqual(1, port.ShowCallCount);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Visible);
    }

    [TestMethod]
    public async Task RequestLockAsyncWhenCalledConcurrentlySerializesAndShowsOnlyOnce()
    {
        var showStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShow = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new RecordingOverlayPort
        {
            ShowOperation = async cancellationToken =>
            {
                showStarted.TrySetResult(true);
                await releaseShow.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            },
        };
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());

        var firstRequest = useCase.RequestLockAsync();
        await showStarted.Task;
        var secondRequest = useCase.RequestLockAsync();
        releaseShow.TrySetResult(true);

        await Task.WhenAll(firstRequest, secondRequest);

        Assert.AreEqual(1, port.ShowCallCount);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Visible);
    }

    [TestMethod]
    public async Task RequestUnlockAsyncAfterLockHidesOnlyOnce()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());
        await useCase.RequestLockAsync();

        await useCase.RequestUnlockAsync();
        await useCase.RequestUnlockAsync();

        Assert.AreEqual(1, port.ShowCallCount);
        Assert.AreEqual(1, port.HideCallCount);
        AssertState(useCase, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task RequestUnlockAsyncFromInitialStateDoesNotCallPort()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());

        await useCase.RequestUnlockAsync();

        Assert.AreEqual(0, port.ShowCallCount);
        Assert.AreEqual(0, port.HideCallCount);
        AssertState(useCase, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task PrepareForExitAsyncAfterLockHidesBeforeReturning()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());
        await useCase.RequestLockAsync();

        await useCase.PrepareForExitAsync();

        CollectionAssert.AreEqual(ExpectedShowThenHideCalls, port.Calls.ToArray());
        AssertState(useCase, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task PrepareForExitAsyncFromInitialStateDoesNotCallPort()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());

        await useCase.PrepareForExitAsync();

        Assert.AreEqual(0, port.ShowCallCount);
        Assert.AreEqual(0, port.HideCallCount);
    }

    [TestMethod]
    public async Task RequestLockAsyncWhenShowFailsRecordsUnknownAndPropagatesFailure()
    {
        var expectedException = new InvalidOperationException("Show failed.");
        var port = new RecordingOverlayPort
        {
            ShowOperation = _ => Task.FromException(expectedException),
        };
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());

        var actualException = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.RequestLockAsync());

        Assert.AreSame(expectedException, actualException);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Unknown);
    }

    [TestMethod]
    public async Task RequestLockAsyncAfterShowFailureRetriesExplicitRequestOnlyAfterOneMinute()
    {
        var attempt = 0;
        var expectedException = new InvalidOperationException("Show failed once.");
        var port = new RecordingOverlayPort
        {
            ShowOperation = _ => Interlocked.Increment(ref attempt) == 1
                ? Task.FromException(expectedException)
                : Task.CompletedTask,
        };
        var clock = new RecoveryTestClock();
        using var useCase = new LockRuntimeUseCase(
            port,
            new RecordingInputPort(),
            clock,
            static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.RequestLockAsync());

        await useCase.RequestLockAsync();
        Assert.AreEqual(1, port.ShowCallCount);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Unknown);

        clock.Advance(TimeSpan.FromMinutes(1));
        await useCase.RequestLockAsync();

        Assert.AreEqual(2, port.ShowCallCount);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Visible);
    }

    [TestMethod]
    public async Task RequestUnlockAsyncAfterShowFailurePerformsHiddenCleanup()
    {
        var port = new RecordingOverlayPort
        {
            ShowOperation = _ => Task.FromException(new InvalidOperationException("Show failed.")),
        };
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.RequestLockAsync());

        await useCase.RequestUnlockAsync();

        Assert.AreEqual(1, port.HideCallCount);
        AssertState(useCase, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task PrepareForExitAsyncAfterHideFailureRetriesCleanup()
    {
        var attempt = 0;
        var expectedException = new InvalidOperationException("Hide failed once.");
        var port = new RecordingOverlayPort
        {
            HideOperation = _ => Interlocked.Increment(ref attempt) == 1
                ? Task.FromException(expectedException)
                : Task.CompletedTask,
        };
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());
        await useCase.RequestLockAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.RequestUnlockAsync());
        AssertState(useCase, LockState.Unlocked, OverlayProjectionState.Unknown);

        await useCase.PrepareForExitAsync();

        Assert.AreEqual(2, port.HideCallCount);
        AssertState(useCase, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task RequestLockAsyncWhenPortCancelsRecordsUnknownAndPropagatesCancellation()
    {
        var port = new RecordingOverlayPort
        {
            ShowOperation = cancellationToken =>
                Task.FromException(new OperationCanceledException(cancellationToken)),
        };
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => useCase.RequestLockAsync());

        AssertState(useCase, LockState.Locked, OverlayProjectionState.Unknown);
    }

    [TestMethod]
    public async Task RequestLockAsyncAfterDisposeThrowsObjectDisposedException()
    {
        var useCase = new LockRuntimeUseCase(new RecordingOverlayPort(), new RecordingInputPort());
        useCase.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => useCase.RequestLockAsync());
    }

    [TestMethod]
    public async Task DisposeDuringInFlightRequestDoesNotReplaceItsResult()
    {
        var showStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShow = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new RecordingOverlayPort
        {
            ShowOperation = async cancellationToken =>
            {
                showStarted.TrySetResult(true);
                await releaseShow.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            },
        };
        var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());
        Task request = useCase.RequestLockAsync();
        await showStarted.Task;

        useCase.Dispose();
        releaseShow.TrySetResult(true);

        await request;
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => useCase.RequestLockAsync());
    }

    [TestMethod]
    public async Task ProjectionInvalidationAfterLockRecordsUnknownWithoutCallingPortAgain()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port, new RecordingInputPort());
        await useCase.RequestLockAsync();

        await useCase.ReportOverlayProjectionInvalidatedAsync(OverlayVisibility.Visible);

        Assert.AreEqual(1, port.ShowCallCount);
        Assert.AreEqual(0, port.HideCallCount);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Unknown);
    }

    [TestMethod]
    [DataRow("regular")]
#if DEBUG
    [DataRow("development")]
#endif
    [DataRow("exit")]
    public async Task UnlockRoutesRestoreInputBeforeRemovingTheOverlay(string route)
    {
        var calls = new List<string>();
        var overlay = new RecordingOverlayPort
        {
            ShowOperation = _ => { calls.Add("Show"); return Task.CompletedTask; },
            HideOperation = _ => { calls.Add("Hide"); return Task.CompletedTask; },
        };
        var input = new RecordingInputPort(calls);
        using var runtime = new LockRuntimeUseCase(overlay, input);

        await runtime.RequestLockAsync();
        await runtime.RequestLockAsync();
        await (route switch
        {
            "regular" => runtime.RequestUnlockAsync(),
#if DEBUG
            "development" => runtime.RequestDevelopmentUnlockAsync(),
#endif
            _ => runtime.PrepareForExitAsync(),
        });

        CollectionAssert.AreEqual(ExpectedLockAndUnlockCalls, calls);
        AssertState(runtime, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task FailedOverlayNeverEnablesInputRestriction()
    {
        var calls = new List<string>();
        var overlay = new RecordingOverlayPort
        {
            ShowOperation = _ => Task.FromException(new InvalidOperationException("Show failed.")),
        };
        using var runtime = new LockRuntimeUseCase(overlay, new RecordingInputPort(calls));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RequestLockAsync());

        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task FailedInputRestrictionIsNotReportedAsSuccessfulLockAndCanBeUnlocked()
    {
        var input = new RecordingInputPort
        {
            EnableOperation = _ => Task.FromException(new InvalidOperationException("Hook failed.")),
        };
        var overlay = new RecordingOverlayPort();
        using var runtime = new LockRuntimeUseCase(overlay, input);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RequestLockAsync());
        AssertState(runtime, LockState.Locked, OverlayProjectionState.Unknown);
        await runtime.RequestUnlockAsync();

        CollectionAssert.AreEqual(ExpectedEnableThenDisableCalls, input.Calls);
        Assert.AreEqual(1, overlay.HideCallCount);
        AssertState(runtime, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task FailedOverlayCleanupStillRestoresInput()
    {
        var input = new RecordingInputPort();
        var overlay = new RecordingOverlayPort
        {
            HideOperation = _ => Task.FromException(new InvalidOperationException("Hide failed.")),
        };
        using var runtime = new LockRuntimeUseCase(overlay, input);
        await runtime.RequestLockAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RequestUnlockAsync());

        CollectionAssert.AreEqual(ExpectedEnableThenDisableCalls, input.Calls);
        AssertState(runtime, LockState.Unlocked, OverlayProjectionState.Unknown);
    }

    [TestMethod]
    public async Task FailedInputCleanupDoesNotReportSuccessfulUnlockAndCanRetry()
    {
        var input = new RecordingInputPort
        {
            DisableOperation = _ => Task.FromException(new InvalidOperationException("Stop failed.")),
        };
        var overlay = new RecordingOverlayPort();
        using var runtime = new LockRuntimeUseCase(overlay, input);
        await runtime.RequestLockAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.RequestUnlockAsync());
        AssertState(runtime, LockState.Unlocked, OverlayProjectionState.Unknown);
        Assert.AreEqual(0, overlay.HideCallCount);

        input.DisableOperation = null;
        await runtime.RequestUnlockAsync();
        AssertState(runtime, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task RelockAfterShutdownCancellationEnablesInputAgain()
    {
        var input = new RecordingInputPort();
        using var runtime = new LockRuntimeUseCase(new RecordingOverlayPort(), input);
        await runtime.RequestLockAsync();
        await runtime.PrepareForExitAsync();

        Assert.IsTrue(await runtime.RestoreLockIfIntentRevisionAsync(runtime.CurrentIntent.Revision));

        CollectionAssert.AreEqual(ExpectedRelockInputCalls, input.Calls);
    }

    private sealed class RecordingInputPort(List<string>? calls = null) : ILockInputPort
    {
        internal List<string> Calls { get; } = calls ?? [];

        internal Func<CancellationToken, Task>? EnableOperation { get; init; }

        internal Func<CancellationToken, Task>? DisableOperation { get; set; }

        public Task EnableAsync(CancellationToken cancellationToken)
        {
            Calls.Add("Enable");
            return EnableOperation?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken)
        {
            Calls.Add("Disable");
            return DisableOperation?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }

    private static void AssertState(
        LockRuntimeUseCase useCase,
        LockState expectedLock,
        OverlayProjectionState expectedProjection)
    {
        Assert.AreEqual(expectedLock, useCase.CurrentState.DesiredLock);
        Assert.AreEqual(expectedProjection, useCase.CurrentState.OverlayProjection);
    }

    private sealed class RecordingOverlayPort : ILockOverlayPort
    {
        private int _showCallCount;
        private int _hideCallCount;

        public Func<CancellationToken, Task>? ShowOperation { get; init; }

        public Func<CancellationToken, Task>? HideOperation { get; init; }

        public int ShowCallCount => Volatile.Read(ref _showCallCount);

        public int HideCallCount => Volatile.Read(ref _hideCallCount);

        public ConcurrentQueue<string> Calls { get; } = new();

        public Task ShowAllAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _showCallCount);
            Calls.Enqueue("Show");
            return ShowOperation?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task HideAllAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _hideCallCount);
            Calls.Enqueue("Hide");
            return HideOperation?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }
}
