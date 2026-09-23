using Zara.Core.Runtime;

namespace Zara.Application.Continuity;

/// <summary>
/// Serializes continuity-state changes and waits for the external supervisor to acknowledge them.
/// </summary>
public interface IRestartContinuityUseCase
{
    /// <summary>
    /// Gets the latest lease whose exact revision was acknowledged, or <see langword="null"/> when
    /// no lease is currently acknowledged.
    /// </summary>
    RestartContinuityLease? CurrentAcknowledgedLease { get; }

    /// <summary>
    /// Gets whether an explicit tray exit has been acknowledged and this use case is terminal.
    /// </summary>
    bool IsReleased { get; }

    /// <summary>
    /// Publishes a changed lock condition without deriving it from overlay visibility.
    /// </summary>
    /// <param name="lockRequired">Whether a restarted process must recover the lock.</param>
    /// <param name="cancellationToken">Cancels the pending publish.</param>
    /// <returns>The lease acknowledged by the supervisor.</returns>
    Task<RestartContinuityLease> PublishLockConditionAsync(
        bool lockRequired,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a changed normal-time restart setting without changing the lock condition.
    /// </summary>
    /// <param name="restartWhenAvailable">
    /// The stored setting, or <see langword="null"/> when it is absent and the ON default applies.
    /// </param>
    /// <param name="cancellationToken">Cancels the pending publish.</param>
    /// <returns>The lease acknowledged by the supervisor.</returns>
    Task<RestartContinuityLease> PublishRestartWhenAvailableAsync(
        bool? restartWhenAvailable,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an overlay observation without changing the independent lock condition.
    /// </summary>
    /// <param name="overlayProjection">The latest known overlay projection.</param>
    /// <param name="cancellationToken">Cancels the pending publish.</param>
    /// <returns>The lease acknowledged by the supervisor.</returns>
    Task<RestartContinuityLease> PublishOverlayProjectionAsync(
        OverlayProjectionState overlayProjection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes supervision for a confirmed tray exit only when lock recovery is not required.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending release before it is acknowledged.</param>
    /// <returns>The release acknowledged by the supervisor.</returns>
    Task<RestartContinuityRelease> ReleaseForExplicitExitAsync(
        CancellationToken cancellationToken = default);
}
