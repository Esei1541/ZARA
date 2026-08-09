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
/// platform request is remembered for the lifetime of this instance and is never repeated.
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
    public Task<ShutdownCancellationRecoveryResult> HandleShutdownCancellationAsync(Guid requestId)
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

            if (!restoreLock)
            {
                return ShutdownCancellationRecoveryResult.LockNotRequired;
            }

            bool restored = await _lockRuntime
                .RestoreLockIfIntentRevisionAsync(
                    expectedIntentRevision,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return restored
                ? ShutdownCancellationRecoveryResult.LockRestored
                : ShutdownCancellationRecoveryResult.SupersededByNewerIntent;
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
                }

                if (_inFlightCancellationRequestId == requestId)
                {
                    _inFlightCancellationRecovery = null;
                    _inFlightCancellationRequestId = Guid.Empty;
                }
            }
        }
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
