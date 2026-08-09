using System.Collections.Concurrent;
using Zara.Application.Locking;
using Zara.Core.Runtime;

namespace Zara.Application.Tests;

[TestClass]
public sealed class LockRuntimeUseCaseTests
{
    private static readonly string[] ExpectedShowThenHideCalls = ["Show", "Hide"];

    [TestMethod]
    public void ConstructorWithNullOverlayPortThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new LockRuntimeUseCase(null!));
    }

    [TestMethod]
    public void CurrentStateBeforeRequestsIsInitialState()
    {
        using var useCase = new LockRuntimeUseCase(new RecordingOverlayPort());

        Assert.AreEqual(RuntimeState.Initial, useCase.CurrentState);
    }

    [TestMethod]
    public async Task RequestLockAsyncShowsOverlayAndConfirmsVisibleState()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port);

        await useCase.RequestLockAsync();

        Assert.AreEqual(1, port.ShowCallCount);
        Assert.AreEqual(0, port.HideCallCount);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Visible);
    }

    [TestMethod]
    public async Task RequestLockAsyncWhenRepeatedSequentiallyShowsOnlyOnce()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port);

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
        using var useCase = new LockRuntimeUseCase(port);

        var firstRequest = useCase.RequestLockAsync();
        await showStarted.Task;
        var secondRequest = useCase.RequestLockAsync();
        releaseShow.TrySetResult(true);

        await Task.WhenAll(firstRequest, secondRequest);

        Assert.AreEqual(1, port.ShowCallCount);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Visible);
    }

    [TestMethod]
    public async Task RequestDevelopmentUnlockAsyncAfterLockHidesOnlyOnce()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port);
        await useCase.RequestLockAsync();

        await useCase.RequestDevelopmentUnlockAsync();
        await useCase.RequestDevelopmentUnlockAsync();

        Assert.AreEqual(1, port.ShowCallCount);
        Assert.AreEqual(1, port.HideCallCount);
        AssertState(useCase, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task RequestDevelopmentUnlockAsyncFromInitialStateDoesNotCallPort()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port);

        await useCase.RequestDevelopmentUnlockAsync();

        Assert.AreEqual(0, port.ShowCallCount);
        Assert.AreEqual(0, port.HideCallCount);
        AssertState(useCase, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task PrepareForExitAsyncAfterLockHidesBeforeReturning()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port);
        await useCase.RequestLockAsync();

        await useCase.PrepareForExitAsync();

        CollectionAssert.AreEqual(ExpectedShowThenHideCalls, port.Calls.ToArray());
        AssertState(useCase, LockState.Unlocked, OverlayProjectionState.Hidden);
    }

    [TestMethod]
    public async Task PrepareForExitAsyncFromInitialStateDoesNotCallPort()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port);

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
        using var useCase = new LockRuntimeUseCase(port);

        var actualException = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.RequestLockAsync());

        Assert.AreSame(expectedException, actualException);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Unknown);
    }

    [TestMethod]
    public async Task RequestLockAsyncAfterShowFailureRetriesExplicitRequest()
    {
        var attempt = 0;
        var expectedException = new InvalidOperationException("Show failed once.");
        var port = new RecordingOverlayPort
        {
            ShowOperation = _ => Interlocked.Increment(ref attempt) == 1
                ? Task.FromException(expectedException)
                : Task.CompletedTask,
        };
        using var useCase = new LockRuntimeUseCase(port);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.RequestLockAsync());

        await useCase.RequestLockAsync();

        Assert.AreEqual(2, port.ShowCallCount);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Visible);
    }

    [TestMethod]
    public async Task RequestDevelopmentUnlockAsyncAfterShowFailurePerformsHiddenCleanup()
    {
        var port = new RecordingOverlayPort
        {
            ShowOperation = _ => Task.FromException(new InvalidOperationException("Show failed.")),
        };
        using var useCase = new LockRuntimeUseCase(port);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.RequestLockAsync());

        await useCase.RequestDevelopmentUnlockAsync();

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
        using var useCase = new LockRuntimeUseCase(port);
        await useCase.RequestLockAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.RequestDevelopmentUnlockAsync());
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
        using var useCase = new LockRuntimeUseCase(port);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => useCase.RequestLockAsync());

        AssertState(useCase, LockState.Locked, OverlayProjectionState.Unknown);
    }

    [TestMethod]
    public async Task RequestLockAsyncAfterDisposeThrowsObjectDisposedException()
    {
        var useCase = new LockRuntimeUseCase(new RecordingOverlayPort());
        useCase.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => useCase.RequestLockAsync());
    }

    [TestMethod]
    public async Task ProjectionInvalidationAfterLockRecordsUnknownWithoutCallingPortAgain()
    {
        var port = new RecordingOverlayPort();
        using var useCase = new LockRuntimeUseCase(port);
        await useCase.RequestLockAsync();

        await useCase.ReportOverlayProjectionInvalidatedAsync(OverlayVisibility.Visible);

        Assert.AreEqual(1, port.ShowCallCount);
        Assert.AreEqual(0, port.HideCallCount);
        AssertState(useCase, LockState.Locked, OverlayProjectionState.Unknown);
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
