using System.Collections.Concurrent;
using Zara.Application.Locking;
using Zara.Application.SystemPower;
using Zara.Core.Runtime;

namespace Zara.Application.Tests;

[TestClass]
public sealed class SystemShutdownUseCaseTests
{
    private static readonly string[] ExpectedPrepareThenShutdownCalls = ["Prepare", "Shutdown"];
    private static readonly string[] ExpectedPrepareThenRestoreCalls = ["Prepare", "RestoreLock"];
    private static readonly string[] ExpectedPrepareShutdownRestoreCalls =
        ["Prepare", "Shutdown", "RestoreLock"];

    [TestMethod]
    public void ConstructorWithNullLockRuntimeThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SystemShutdownUseCase(null!, new RecordingShutdownPort()));
    }

    [TestMethod]
    public void ConstructorWithNullShutdownPortThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SystemShutdownUseCase(new RecordingLockRuntime(initiallyLocked: false), null!));
    }

    [TestMethod]
    public async Task RequestShutdownAsyncPreparesRuntimeBeforeRequestingNormalShutdown()
    {
        var calls = new ConcurrentQueue<string>();
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true, calls);
        var shutdownPort = new RecordingShutdownPort(calls);
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        await useCase.RequestShutdownAsync();

        CollectionAssert.AreEqual(ExpectedPrepareThenShutdownCalls, calls.ToArray());
        Assert.AreEqual(1, lockRuntime.PrepareForExitCallCount);
        Assert.AreEqual(0, lockRuntime.RequestLockCallCount);
        Assert.AreEqual(1, shutdownPort.RequestShutdownCallCount);
    }

    [TestMethod]
    public async Task RequestShutdownAsyncWhenRepeatedAfterAcceptanceRequestsShutdownOnlyOnce()
    {
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        var shutdownPort = new RecordingShutdownPort();
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        await useCase.RequestShutdownAsync();
        await useCase.RequestShutdownAsync();

        Assert.AreEqual(1, lockRuntime.PrepareForExitCallCount);
        Assert.AreEqual(1, shutdownPort.RequestShutdownCallCount);
    }

    [TestMethod]
    public async Task RequestShutdownAsyncWhenCalledConcurrentlyRequestsShutdownOnlyOnce()
    {
        var shutdownStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        var shutdownPort = new RecordingShutdownPort
        {
            RequestOperation = async cancellationToken =>
            {
                shutdownStarted.TrySetResult(true);
                await releaseShutdown.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            },
        };
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        Task firstRequest = useCase.RequestShutdownAsync();
        await shutdownStarted.Task;
        Task secondRequest = useCase.RequestShutdownAsync();
        releaseShutdown.TrySetResult(true);

        await Task.WhenAll(firstRequest, secondRequest);

        Assert.AreEqual(1, lockRuntime.PrepareForExitCallCount);
        Assert.AreEqual(1, shutdownPort.RequestShutdownCallCount);
    }

    [TestMethod]
    public async Task ConcurrentCallersShareTheSameFailedShutdownAttempt()
    {
        var shutdownStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedException = new InvalidOperationException("Shutdown was rejected.");
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        var shutdownPort = new RecordingShutdownPort
        {
            RequestOperation = async cancellationToken =>
            {
                shutdownStarted.TrySetResult(true);
                await releaseShutdown.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw expectedException;
            },
        };
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        Task firstRequest = useCase.RequestShutdownAsync();
        await shutdownStarted.Task;
        Task secondRequest = useCase.RequestShutdownAsync();
        releaseShutdown.TrySetResult(true);

        InvalidOperationException firstException =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => firstRequest);
        InvalidOperationException secondException =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => secondRequest);

        Assert.AreSame(firstRequest, secondRequest);
        Assert.AreSame(expectedException, firstException);
        Assert.AreSame(firstException, secondException);
        Assert.AreEqual(1, lockRuntime.PrepareForExitCallCount);
        Assert.AreEqual(1, lockRuntime.RequestLockCallCount);
        Assert.AreEqual(1, shutdownPort.RequestShutdownCallCount);
    }

    [TestMethod]
    public async Task RequestShutdownAsyncWhenCleanupFailsRestoresPreviouslyRequestedLock()
    {
        var calls = new ConcurrentQueue<string>();
        var expectedException = new InvalidOperationException("Cleanup failed.");
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true, calls)
        {
            PrepareForExitOperation = _ => Task.FromException(expectedException),
        };
        var shutdownPort = new RecordingShutdownPort(calls);
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        InvalidOperationException actualException =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => useCase.RequestShutdownAsync());

        Assert.AreSame(expectedException, actualException);
        CollectionAssert.AreEqual(ExpectedPrepareThenRestoreCalls, calls.ToArray());
        Assert.AreEqual(0, shutdownPort.RequestShutdownCallCount);
        Assert.AreEqual(CancellationToken.None, lockRuntime.LastRequestLockCancellationToken);
    }

    [TestMethod]
    public async Task RequestShutdownAsyncWhenCleanupIsCancelledRestoresLockWithoutCallerCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        var expectedException = new OperationCanceledException(cancellationSource.Token);
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true)
        {
            PrepareForExitOperation = _ => Task.FromException(expectedException),
        };
        var shutdownPort = new RecordingShutdownPort();
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        OperationCanceledException actualException =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => useCase.RequestShutdownAsync(cancellationSource.Token));

        Assert.AreSame(expectedException, actualException);
        Assert.AreEqual(1, lockRuntime.RequestLockCallCount);
        Assert.AreEqual(CancellationToken.None, lockRuntime.LastRequestLockCancellationToken);
        Assert.AreEqual(0, shutdownPort.RequestShutdownCallCount);
    }

    [TestMethod]
    public async Task RequestShutdownAsyncWhenPortFailsRestoresPreviouslyRequestedLock()
    {
        var calls = new ConcurrentQueue<string>();
        var expectedException = new InvalidOperationException("Shutdown was rejected.");
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true, calls);
        var shutdownPort = new RecordingShutdownPort(calls)
        {
            RequestOperation = _ => Task.FromException(expectedException),
        };
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        InvalidOperationException actualException =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => useCase.RequestShutdownAsync());

        Assert.AreSame(expectedException, actualException);
        CollectionAssert.AreEqual(ExpectedPrepareShutdownRestoreCalls, calls.ToArray());
        Assert.AreEqual(1, lockRuntime.RequestLockCallCount);
        Assert.AreEqual(CancellationToken.None, lockRuntime.LastRequestLockCancellationToken);
    }

    [TestMethod]
    public async Task RequestShutdownAsyncWhenPortIsCancelledRestoresPreviouslyRequestedLock()
    {
        using var cancellationSource = new CancellationTokenSource();
        var expectedException = new OperationCanceledException(cancellationSource.Token);
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        var shutdownPort = new RecordingShutdownPort
        {
            RequestOperation = _ => Task.FromException(expectedException),
        };
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        OperationCanceledException actualException =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => useCase.RequestShutdownAsync(cancellationSource.Token));

        Assert.AreSame(expectedException, actualException);
        Assert.AreEqual(1, lockRuntime.RequestLockCallCount);
        Assert.AreEqual(CancellationToken.None, lockRuntime.LastRequestLockCancellationToken);
    }

    [TestMethod]
    public async Task NewerSafetyUnlockIntentPreventsFailedShutdownFromRestoringOldLock()
    {
        var shutdownStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedException = new InvalidOperationException("Shutdown was rejected.");
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        var shutdownPort = new RecordingShutdownPort
        {
            RequestOperation = async cancellationToken =>
            {
                shutdownStarted.TrySetResult(true);
                await releaseShutdown.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw expectedException;
            },
        };
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        Task shutdownRequest = useCase.RequestShutdownAsync();
        await shutdownStarted.Task;
        await lockRuntime.RequestDevelopmentUnlockAsync();
        releaseShutdown.TrySetResult(true);

        InvalidOperationException actualException =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => shutdownRequest);

        Assert.AreSame(expectedException, actualException);
        Assert.AreEqual(1, lockRuntime.ConditionalRestoreCallCount);
        Assert.AreEqual(0, lockRuntime.RequestLockCallCount);
        Assert.AreEqual(
            new LockIntentSnapshot(LockState.Unlocked, Revision: 2),
            lockRuntime.CurrentIntent);
    }

    [TestMethod]
    public async Task RequestShutdownAsyncWhenNoLockWasRequestedDoesNotCompensateFailure()
    {
        var expectedException = new InvalidOperationException("Shutdown was rejected.");
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: false);
        var shutdownPort = new RecordingShutdownPort
        {
            RequestOperation = _ => Task.FromException(expectedException),
        };
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        InvalidOperationException actualException =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => useCase.RequestShutdownAsync());

        Assert.AreSame(expectedException, actualException);
        Assert.AreEqual(0, lockRuntime.RequestLockCallCount);
    }

    [TestMethod]
    public async Task RequestShutdownAsyncWhenCompensationFailsAggregatesBothFailures()
    {
        var operationException = new InvalidOperationException("Shutdown was rejected.");
        var compensationException = new InvalidOperationException("Lock restore failed.");
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true)
        {
            RequestLockOperation = _ => Task.FromException(compensationException),
        };
        var shutdownPort = new RecordingShutdownPort
        {
            RequestOperation = _ => Task.FromException(operationException),
        };
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);

        AggregateException aggregateException =
            await Assert.ThrowsExactlyAsync<AggregateException>(
                () => useCase.RequestShutdownAsync());

        Assert.HasCount(2, aggregateException.InnerExceptions);
        Assert.AreSame(operationException, aggregateException.InnerExceptions[0]);
        Assert.AreSame(compensationException, aggregateException.InnerExceptions[1]);
    }

    [TestMethod]
    public async Task RequestShutdownAsyncAfterFailedAttemptCanRetryExplicitly()
    {
        var attempt = 0;
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        var shutdownPort = new RecordingShutdownPort
        {
            RequestOperation = _ => Interlocked.Increment(ref attempt) == 1
                ? Task.FromException(new InvalidOperationException("Shutdown was rejected once."))
                : Task.CompletedTask,
        };
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.RequestShutdownAsync());

        await useCase.RequestShutdownAsync();
        await useCase.RequestShutdownAsync();

        Assert.AreEqual(2, lockRuntime.PrepareForExitCallCount);
        Assert.AreEqual(1, lockRuntime.RequestLockCallCount);
        Assert.AreEqual(2, shutdownPort.RequestShutdownCallCount);
    }

    [TestMethod]
    public async Task RequestShutdownAsyncAfterDisposeThrowsObjectDisposedException()
    {
        var useCase = new SystemShutdownUseCase(
            new RecordingLockRuntime(initiallyLocked: false),
            new RecordingShutdownPort());
        useCase.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => useCase.RequestShutdownAsync());
    }

    [TestMethod]
    public async Task HandleShutdownCancellationAsyncAfterAcceptedRequestRestoresPreviousLock()
    {
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        var shutdownPort = new RecordingShutdownPort();
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);
        await useCase.RequestShutdownAsync();

        ShutdownCancellationRecoveryResult result =
            await useCase.HandleShutdownCancellationAsync();

        Assert.AreEqual(ShutdownCancellationRecoveryResult.LockRestored, result);
        Assert.AreEqual(1, lockRuntime.ConditionalRestoreCallCount);
        Assert.AreEqual(CancellationToken.None, lockRuntime.LastRequestLockCancellationToken);
        Assert.AreEqual(LockState.Locked, lockRuntime.CurrentState.DesiredLock);
    }

    [TestMethod]
    public async Task HandleShutdownCancellationAsyncAfterNewerUnlockDoesNotRestoreStaleLock()
    {
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        using var useCase = new SystemShutdownUseCase(
            lockRuntime,
            new RecordingShutdownPort());
        await useCase.RequestShutdownAsync();
        await lockRuntime.RequestDevelopmentUnlockAsync();

        ShutdownCancellationRecoveryResult result =
            await useCase.HandleShutdownCancellationAsync();

        Assert.AreEqual(ShutdownCancellationRecoveryResult.SupersededByNewerIntent, result);
        Assert.AreEqual(1, lockRuntime.ConditionalRestoreCallCount);
        Assert.AreEqual(0, lockRuntime.RequestLockCallCount);
        Assert.AreEqual(LockState.Unlocked, lockRuntime.CurrentState.DesiredLock);
    }

    [TestMethod]
    public async Task HandleShutdownCancellationAsyncWithoutAcceptedRequestDoesNothing()
    {
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        using var useCase = new SystemShutdownUseCase(
            lockRuntime,
            new RecordingShutdownPort());

        ShutdownCancellationRecoveryResult result =
            await useCase.HandleShutdownCancellationAsync();

        Assert.AreEqual(ShutdownCancellationRecoveryResult.NoAcceptedRequest, result);
        Assert.AreEqual(0, lockRuntime.ConditionalRestoreCallCount);
    }

    [TestMethod]
    public async Task HandleShutdownCancellationAsyncWhenCalledConcurrentlyRestoresOnlyOnce()
    {
        var restoreStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true)
        {
            RequestLockOperation = async cancellationToken =>
            {
                restoreStarted.TrySetResult(true);
                await releaseRestore.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            },
        };
        using var useCase = new SystemShutdownUseCase(
            lockRuntime,
            new RecordingShutdownPort());
        await useCase.RequestShutdownAsync();

        Task<ShutdownCancellationRecoveryResult> firstRecovery =
            useCase.HandleShutdownCancellationAsync();
        await restoreStarted.Task;
        Task<ShutdownCancellationRecoveryResult> secondRecovery =
            useCase.HandleShutdownCancellationAsync();
        releaseRestore.TrySetResult(true);

        ShutdownCancellationRecoveryResult[] results =
            await Task.WhenAll(firstRecovery, secondRecovery);

        CollectionAssert.AreEqual(
            new[]
            {
                ShutdownCancellationRecoveryResult.LockRestored,
                ShutdownCancellationRecoveryResult.LockRestored,
            },
            results);
        Assert.AreEqual(1, lockRuntime.ConditionalRestoreCallCount);
    }

    [TestMethod]
    public async Task HandleShutdownCancellationAsyncClearsAcceptanceSoLaterRequestCanRetry()
    {
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        var shutdownPort = new RecordingShutdownPort();
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);
        await useCase.RequestShutdownAsync();
        await useCase.HandleShutdownCancellationAsync();

        await useCase.RequestShutdownAsync();

        Assert.AreEqual(2, shutdownPort.RequestShutdownCallCount);
        Assert.AreEqual(2, lockRuntime.PrepareForExitCallCount);
    }

    [TestMethod]
    public async Task StaleCancellationIdentityDoesNotClearNewAcceptedRequest()
    {
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        var shutdownPort = new RecordingShutdownPort();
        using var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);
        Guid firstRequestId = Guid.NewGuid();
        Guid secondRequestId = Guid.NewGuid();
        await useCase.RequestShutdownAsync(firstRequestId);
        await useCase.HandleShutdownCancellationAsync(firstRequestId);
        await useCase.RequestShutdownAsync(secondRequestId);

        ShutdownCancellationRecoveryResult staleResult =
            await useCase.HandleShutdownCancellationAsync(firstRequestId);
        ShutdownCancellationRecoveryResult currentResult =
            await useCase.HandleShutdownCancellationAsync(secondRequestId);

        Assert.AreEqual(ShutdownCancellationRecoveryResult.NoAcceptedRequest, staleResult);
        Assert.AreEqual(ShutdownCancellationRecoveryResult.LockRestored, currentResult);
        Assert.AreEqual(2, shutdownPort.RequestShutdownCallCount);
        Assert.AreEqual(2, lockRuntime.ConditionalRestoreCallCount);
    }

    [TestMethod]
    public async Task DisposeDuringInFlightRequestAllowsSharedRequestToFinish()
    {
        var shutdownStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseShutdown = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lockRuntime = new RecordingLockRuntime(initiallyLocked: true);
        var shutdownPort = new RecordingShutdownPort
        {
            RequestOperation = async cancellationToken =>
            {
                shutdownStarted.TrySetResult(true);
                await releaseShutdown.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            },
        };
        var useCase = new SystemShutdownUseCase(lockRuntime, shutdownPort);
        Task request = useCase.RequestShutdownAsync();
        await shutdownStarted.Task;

        useCase.Dispose();
        releaseShutdown.TrySetResult(true);

        await request;
        Assert.AreEqual(1, shutdownPort.RequestShutdownCallCount);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => useCase.RequestShutdownAsync());
    }

    private sealed class RecordingShutdownPort : ISystemShutdownPort
    {
        private readonly ConcurrentQueue<string>? _calls;
        private int _requestShutdownCallCount;

        public RecordingShutdownPort(ConcurrentQueue<string>? calls = null)
        {
            _calls = calls;
        }

        public Func<CancellationToken, Task>? RequestOperation { get; init; }

        public int RequestShutdownCallCount => Volatile.Read(ref _requestShutdownCallCount);

        public Task RequestShutdownAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestShutdownCallCount);
            _calls?.Enqueue("Shutdown");
            return RequestOperation?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class RecordingLockRuntime : ILockRuntimeUseCase
    {
        private readonly ConcurrentQueue<string>? _calls;
        private readonly object _intentSync = new();
        private RuntimeState _currentState;
        private LockState _desiredIntent;
        private long _intentRevision;
        private int _conditionalRestoreCallCount;
        private int _prepareForExitCallCount;
        private int _requestLockCallCount;

        public RecordingLockRuntime(
            bool initiallyLocked,
            ConcurrentQueue<string>? calls = null)
        {
            _calls = calls;
            _desiredIntent = initiallyLocked ? LockState.Locked : LockState.Unlocked;
            _currentState = initiallyLocked
                ? new RuntimeState(LockState.Locked, OverlayProjectionState.Visible)
                : RuntimeState.Initial;
        }

        public Func<CancellationToken, Task>? PrepareForExitOperation { get; init; }

        public Func<CancellationToken, Task>? RequestLockOperation { get; init; }

        public RuntimeState CurrentState => Volatile.Read(ref _currentState);

        public LockIntentSnapshot CurrentIntent
        {
            get
            {
                lock (_intentSync)
                {
                    return new LockIntentSnapshot(
                        _desiredIntent,
                        _intentRevision);
                }
            }
        }

        public int ConditionalRestoreCallCount => Volatile.Read(ref _conditionalRestoreCallCount);

        public int PrepareForExitCallCount => Volatile.Read(ref _prepareForExitCallCount);

        public int RequestLockCallCount => Volatile.Read(ref _requestLockCallCount);

        public CancellationToken LastRequestLockCancellationToken { get; private set; }

        public async Task RequestLockAsync(CancellationToken cancellationToken = default)
        {
            lock (_intentSync)
            {
                _desiredIntent = LockState.Locked;
                _intentRevision++;
            }

            Interlocked.Increment(ref _requestLockCallCount);
            LastRequestLockCancellationToken = cancellationToken;
            _calls?.Enqueue("RestoreLock");

            if (RequestLockOperation is not null)
            {
                await RequestLockOperation(cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(
                ref _currentState,
                new RuntimeState(LockState.Locked, OverlayProjectionState.Visible));
        }

        public Task RequestDevelopmentUnlockAsync(CancellationToken cancellationToken = default)
            => RequestUnlockAsync(cancellationToken);

        public Task RequestUnlockAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_intentSync)
            {
                _desiredIntent = LockState.Unlocked;
                _intentRevision++;
                Volatile.Write(ref _currentState, RuntimeState.Initial);
            }

            return Task.CompletedTask;
        }

        public async Task<bool> RestoreLockIfIntentRevisionAsync(
            long expectedIntentRevision,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _conditionalRestoreCallCount);
            lock (_intentSync)
            {
                if (_intentRevision != expectedIntentRevision)
                {
                    return false;
                }

                _desiredIntent = LockState.Locked;
                _intentRevision++;
            }

            Interlocked.Increment(ref _requestLockCallCount);
            LastRequestLockCancellationToken = cancellationToken;
            _calls?.Enqueue("RestoreLock");

            if (RequestLockOperation is not null)
            {
                await RequestLockOperation(cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(
                ref _currentState,
                new RuntimeState(LockState.Locked, OverlayProjectionState.Visible));
            return true;
        }

        public Task ReportOverlayProjectionInvalidatedAsync(
            OverlayVisibility visibility,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task PrepareForExitAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _prepareForExitCallCount);
            _calls?.Enqueue("Prepare");
            lock (_intentSync)
            {
                _desiredIntent = LockState.Unlocked;
                _intentRevision++;
            }

            if (PrepareForExitOperation is not null)
            {
                await PrepareForExitOperation(cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _currentState, RuntimeState.Initial);
        }
    }
}
