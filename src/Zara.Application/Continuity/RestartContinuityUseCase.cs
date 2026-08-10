using Zara.Core.Continuity;
using Zara.Core.Runtime;

namespace Zara.Application.Continuity;

/// <summary>
/// Owns the acknowledged continuity snapshot and serializes every lease or release through its ACK.
/// </summary>
/// <remarks>
/// A failed publish consumes its revision and retains the desired local inputs, but does not replace
/// the last acknowledged lease. Lock conditions and overlay projections are updated independently
/// so temporary overlay cleanup cannot silently revoke a still-active lock condition.
/// </remarks>
public sealed class RestartContinuityUseCase : IRestartContinuityUseCase, IDisposable
{
    private readonly IRestartContinuityPort _port;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private ContinuityState _currentState;
    private RestartContinuityLease? _acknowledgedLease;
    private RestartContinuityRelease? _acknowledgedRelease;
    private long _nextRevision;
    private int _released;
    private int _disposed;

    /// <summary>
    /// Initializes continuity publishing with no required lock, a hidden overlay, and the supplied
    /// normal-time setting.
    /// </summary>
    /// <param name="port">The external supervisor boundary.</param>
    /// <param name="restartWhenAvailable">
    /// The stored normal-time restart setting, or <see langword="null"/> when absent and the ON
    /// product default must be used.
    /// </param>
    public RestartContinuityUseCase(
        IRestartContinuityPort port,
        bool? restartWhenAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(port);

        _port = port;
        _currentState = new ContinuityState(
            LockRequired: false,
            RestartWhenAvailable: restartWhenAvailable ??
                RestartContinuityPolicy.DefaultRestartWhenAvailable,
            OverlayProjection: OverlayProjectionState.Hidden);
    }

    /// <inheritdoc />
    public RestartContinuityLease? CurrentAcknowledgedLease =>
        Volatile.Read(ref _acknowledgedLease);

    /// <inheritdoc />
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <inheritdoc />
    public Task<RestartContinuityLease> PublishLockConditionAsync(
        bool lockRequired,
        CancellationToken cancellationToken = default) =>
        UpdateAndPublishAsync(
            state => state with { LockRequired = lockRequired },
            cancellationToken);

    /// <inheritdoc />
    public Task<RestartContinuityLease> PublishRestartWhenAvailableAsync(
        bool? restartWhenAvailable,
        CancellationToken cancellationToken = default) =>
        UpdateAndPublishAsync(
            state => state with
            {
                RestartWhenAvailable = restartWhenAvailable ??
                    RestartContinuityPolicy.DefaultRestartWhenAvailable,
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<RestartContinuityLease> PublishOverlayProjectionAsync(
        OverlayProjectionState overlayProjection,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(overlayProjection))
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlayProjection),
                overlayProjection,
                "The overlay projection is not defined.");
        }

        return UpdateAndPublishAsync(
            state => state with { OverlayProjection = overlayProjection },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RestartContinuityRelease> ReleaseForExplicitExitAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            if (_acknowledgedRelease is not null)
            {
                return _acknowledgedRelease;
            }

            var release = new RestartContinuityRelease(NextRevision());
            RestartContinuityAcknowledgement acknowledgement =
                await _port.ReleaseAsync(release, cancellationToken).ConfigureAwait(false);
            ValidateAcknowledgement(release.Revision, acknowledgement);

            _acknowledgedRelease = release;
            Volatile.Write(ref _acknowledgedLease, null);
            Volatile.Write(ref _released, 1);
            return release;
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>
    /// Prevents new operations while allowing an already serialized publish to release its gate.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Disposing SemaphoreSlim here can race an in-flight operation whose finally block still
        // has to release the gate. The short-lived process owns this use case for its lifetime.
        GC.SuppressFinalize(this);
    }

    private async Task<RestartContinuityLease> UpdateAndPublishAsync(
        Func<ContinuityState, ContinuityState> update,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            ThrowIfReleased();

            ContinuityState nextState = update(_currentState);
            _currentState = nextState;
            RestartContinuityDecision decision = RestartContinuityPolicy.Decide(
                nextState.LockRequired,
                nextState.RestartWhenAvailable);
            var lease = new RestartContinuityLease(
                NextRevision(),
                nextState.LockRequired,
                nextState.RestartWhenAvailable,
                nextState.OverlayProjection,
                decision);

            RestartContinuityAcknowledgement acknowledgement =
                await _port.PublishAsync(lease, cancellationToken).ConfigureAwait(false);
            ValidateAcknowledgement(lease.Revision, acknowledgement);

            Volatile.Write(ref _acknowledgedLease, lease);
            return lease;
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private long NextRevision() => checked(++_nextRevision);

    private static void ValidateAcknowledgement(
        long expectedRevision,
        RestartContinuityAcknowledgement? acknowledgement)
    {
        if (acknowledgement?.Revision != expectedRevision)
        {
            throw new InvalidOperationException(
                $"The continuity supervisor did not acknowledge revision {expectedRevision}.");
        }
    }

    private void ThrowIfReleased()
    {
        if (IsReleased)
        {
            throw new InvalidOperationException(
                "Continuity supervision was released for an explicit process exit.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record ContinuityState(
        bool LockRequired,
        bool RestartWhenAvailable,
        OverlayProjectionState OverlayProjection);
}
