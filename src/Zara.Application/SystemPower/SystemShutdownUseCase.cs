using Zara.Application.Locking;
using Zara.Core.Runtime;

namespace Zara.Application.SystemPower;

/// <summary>
/// Serializes system-shutdown requests, cleans up lock effects, and restores a previously requested
/// lock when cleanup or the platform request fails.
/// </summary>
/// <remarks>
/// Compensation intentionally uses <see cref="CancellationToken.None"/> so caller cancellation
/// cannot leave a lock that was requested before this operation silently removed. A successful
/// platform request is remembered until its cancellation is confirmed and is not repeated while pending.
/// </remarks>
public sealed class SystemShutdownUseCase : ISystemShutdownUseCase, IDisposable
{
    private readonly ILockRuntimeUseCase _lockRuntime;
    private readonly ISystemShutdownPort _shutdownPort;
    private readonly object _requestSync = new();
    private Task? _inFlightRequest;
    private Guid _inFlightRequestId;
    private Task<ShutdownCancellationRecoveryResult>? _inFlightCancellationRecovery;
    private Guid _inFlightCancellationRequestId;
    private bool _shutdownRequestAccepted;
    private Guid _acceptedRequestId;
    private bool _restoreLockAfterAcceptedCancellation;
    private long _acceptedCancellationIntentRevision;
    private ShutdownCancellationRecoveryResult? _acceptedRecoveryResult;
    private LockIntentSnapshot? _recoveredIntent;
    private bool _disposed;

    /// <summary>
    /// Initializes a system-shutdown coordinator.
    /// </summary>
    /// <param name="lockRuntime">The lock runtime that must be made safe before shutdown.</param>
    /// <param name="shutdownPort">The platform adapter that requests normal system shutdown.</param>
    public SystemShutdownUseCase(
        ILockRuntimeUseCase lockRuntime,
        ISystemShutdownPort shutdownPort)
    {
        ArgumentNullException.ThrowIfNull(lockRuntime);
        ArgumentNullException.ThrowIfNull(shutdownPort);

        _lockRuntime = lockRuntime;
        _shutdownPort = shutdownPort;
    }

    /// <inheritdoc />
    public Task RequestShutdownAsync(CancellationToken cancellationToken = default) =>
        RequestShutdownAsync(Guid.NewGuid(), cancellationToken);

    /// <inheritdoc />
    public Task RequestShutdownAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty shutdown request identity is required.", nameof(requestId));
        }

        TaskCompletionSource<bool> startSignal;
        Task request;

        lock (_requestSync)
        {
            ThrowIfDisposed();
            if (_shutdownRequestAccepted)
            {
                return Task.CompletedTask;
            }

            if (_inFlightRequest is not null)
            {
                return _inFlightRequest;
            }

            startSignal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            request = RunSharedRequestAsync(startSignal.Task, requestId, cancellationToken);
            _inFlightRequest = request;
            _inFlightRequestId = requestId;
        }

        startSignal.TrySetResult(true);
        return request;
    }

    /// <inheritdoc />
    public Task<ShutdownCancellationRecoveryResult> HandleShutdownCancellationAsync()
    {
        Guid requestId;
        lock (_requestSync)
        {
            ThrowIfDisposed();
            requestId = _shutdownRequestAccepted
                ? _acceptedRequestId
                : _inFlightRequestId;
        }

        return requestId == Guid.Empty
            ? Task.FromResult(ShutdownCancellationRecoveryResult.NoAcceptedRequest)
            : HandleShutdownCancellationAsync(requestId);
    }

    /// <inheritdoc />
    public async Task<ShutdownCancellationRecoveryResult> HandleShutdownCancellationAsync(Guid requestId)
    {
        try
        {
            return await RestoreLockWhileShutdownPendingAsync(requestId).ConfigureAwait(false);
        }
        finally
        {
            lock (_requestSync)
            {
                if (_acceptedRequestId == requestId)
                {
                    _shutdownRequestAccepted = false;
                    _acceptedRequestId = Guid.Empty;
                    _restoreLockAfterAcceptedCancellation = false;
                    _acceptedCancellationIntentRevision = 0;
                    _acceptedRecoveryResult = null;
                    _recoveredIntent = null;
                }
            }
        }
    }

    /// <inheritdoc />
    public Task<ShutdownCancellationRecoveryResult> RestoreLockWhileShutdownPendingAsync(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty shutdown request identity is required.", nameof(requestId));
        }

        TaskCompletionSource<bool> startSignal;
        Task<ShutdownCancellationRecoveryResult> recovery;

        lock (_requestSync)
        {
            ThrowIfDisposed();
            bool matchesAcceptedRequest =
                _shutdownRequestAccepted && _acceptedRequestId == requestId;
            bool matchesInFlightRequest =
                _inFlightRequest is not null && _inFlightRequestId == requestId;
            if (!matchesAcceptedRequest && !matchesInFlightRequest)
            {
                return Task.FromResult(ShutdownCancellationRecoveryResult.NoAcceptedRequest);
            }

            if (_acceptedRecoveryResult is { } priorResult)
            {
                return Task.FromResult(ResolveRecoveryResult(priorResult));
            }

            if (_inFlightCancellationRecovery is not null)
            {
                return _inFlightCancellationRequestId == requestId
                    ? _inFlightCancellationRecovery
                    : Task.FromResult(ShutdownCancellationRecoveryResult.NoAcceptedRequest);
            }

            startSignal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            recovery = RunSharedCancellationRecoveryAsync(startSignal.Task, requestId);
            _inFlightCancellationRecovery = recovery;
            _inFlightCancellationRequestId = requestId;
        }

        startSignal.TrySetResult(true);
        return recovery;
    }

    /// <summary>
    /// Prevents new shutdown requests while allowing an already shared request to finish safely.
    /// </summary>
    public void Dispose()
    {
        lock (_requestSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private async Task RunSharedRequestAsync(
        Task startSignal,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await startSignal.ConfigureAwait(false);

        try
        {
            LockIntentSnapshot originalIntent = _lockRuntime.CurrentIntent;
            long compensationRevision = checked(originalIntent.Revision + 1);
            await RequestShutdownCoreAsync(
                    originalIntent.DesiredLock == LockState.Locked,
                    compensationRevision,
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_requestSync)
            {
                _shutdownRequestAccepted = true;
                _acceptedRequestId = requestId;
                _restoreLockAfterAcceptedCancellation =
                    originalIntent.DesiredLock == LockState.Locked;
                _acceptedCancellationIntentRevision = compensationRevision;
            }
        }
        finally
        {
            lock (_requestSync)
            {
                _inFlightRequest = null;
                _inFlightRequestId = Guid.Empty;
            }
        }
    }

    private async Task<ShutdownCancellationRecoveryResult> RunSharedCancellationRecoveryAsync(
        Task startSignal,
        Guid requestId)
    {
        await startSignal.ConfigureAwait(false);

        try
        {
            Task? request;
            lock (_requestSync)
            {
                request = _inFlightRequestId == requestId
                    ? _inFlightRequest
                    : null;
            }

            if (request is not null)
            {
                try
                {
                    await request.ConfigureAwait(false);
                }
                catch
                {
                    return ShutdownCancellationRecoveryResult.NoAcceptedRequest;
                }
            }

            bool restoreLock;
            long expectedIntentRevision;
            lock (_requestSync)
            {
                if (!_shutdownRequestAccepted || _acceptedRequestId != requestId)
                {
                    return ShutdownCancellationRecoveryResult.NoAcceptedRequest;
                }

                restoreLock = _restoreLockAfterAcceptedCancellation;
                expectedIntentRevision = _acceptedCancellationIntentRevision;
            }

            bool restored = false;
            bool recoveryPending = false;
            var restoredIntent = new LockIntentSnapshot(
                LockState.Locked,
                checked(expectedIntentRevision + 1));
#pragma warning disable CA1031 // Once the owned lock intent is restored, the runtime owns failed-effect retries.
            try
            {
                restored = restoreLock && await _lockRuntime
                        .RestoreLockIfIntentRevisionAsync(
                            expectedIntentRevision,
                            CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not ObjectDisposedException &&
                restoreLock && _lockRuntime.CurrentIntent == restoredIntent)
            {
                restored = true;
                recoveryPending = true;
            }
#pragma warning restore CA1031
            ShutdownCancellationRecoveryResult result = !restoreLock
                ? ShutdownCancellationRecoveryResult.LockNotRequired
                : restored
                    ? recoveryPending
                        ? ShutdownCancellationRecoveryResult.RecoveryPending
                        : ShutdownCancellationRecoveryResult.LockRestored
                    : ShutdownCancellationRecoveryResult.SupersededByNewerIntent;
            lock (_requestSync)
            {
                if (_acceptedRequestId == requestId)
                {
                    _acceptedRecoveryResult = result;
                    _recoveredIntent = new LockIntentSnapshot(
                        restoreLock ? LockState.Locked : LockState.Unlocked,
                        restored ? checked(expectedIntentRevision + 1) : expectedIntentRevision);
                    result = ResolveRecoveryResult(result);
                }
            }

            return result;
        }
        finally
        {
            lock (_requestSync)
            {
                if (_inFlightCancellationRequestId == requestId)
                {
                    _inFlightCancellationRecovery = null;
                    _inFlightCancellationRequestId = Guid.Empty;
                }
            }
        }
    }

    private ShutdownCancellationRecoveryResult ResolveRecoveryResult(
        ShutdownCancellationRecoveryResult result)
    {
        if (_recoveredIntent is { } recoveredIntent && recoveredIntent != _lockRuntime.CurrentIntent)
        {
            return ShutdownCancellationRecoveryResult.SupersededByNewerIntent;
        }

        return result is ShutdownCancellationRecoveryResult.LockRestored or
            ShutdownCancellationRecoveryResult.RecoveryPending
                ? _lockRuntime.CurrentState.OverlayProjection == OverlayProjectionState.Visible
                    ? ShutdownCancellationRecoveryResult.LockRestored
                    : ShutdownCancellationRecoveryResult.RecoveryPending
                : result;
    }

    private async Task RequestShutdownCoreAsync(
        bool restoreLockOnFailure,
        long compensationRevision,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA1031 // Any primary failure must trigger safety compensation before propagation.
        try
        {
            await _lockRuntime.PrepareForExitAsync(cancellationToken).ConfigureAwait(false);
            await _shutdownPort.RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception operationException)
        {
            if (!restoreLockOnFailure)
            {
                throw;
            }

            try
            {
                _ = await _lockRuntime
                    .RestoreLockIfIntentRevisionAsync(
                        compensationRevision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception compensationException)
            {
                throw new AggregateException(
                    "System shutdown failed and the previous lock request could not be restored.",
                    operationException,
                    compensationException);
            }

            throw;
        }
#pragma warning restore CA1031
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
