namespace Zara.Application.Locking;

/// <summary>
/// Projects the requested lock-overlay visibility into the current user session.
/// </summary>
/// <remarks>
/// Implementations must make both operations idempotent. Showing reconciles exactly one overlay
/// per current display and keeps that projection aligned with display-topology changes. Hiding
/// stops topology observation and removes every overlay before completing. Implementations must
/// not report success after a partial projection.
/// </remarks>
public interface ILockOverlayPort
{
    /// <summary>
    /// Shows or reconciles lock overlays across all current displays.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending projection operation.</param>
    /// <returns>A task that completes after the projection has been confirmed.</returns>
    Task ShowAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes every lock overlay and stops maintaining the visible projection.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending projection operation.</param>
    /// <returns>A task that completes after no overlay remains.</returns>
    Task HideAllAsync(CancellationToken cancellationToken);
}
