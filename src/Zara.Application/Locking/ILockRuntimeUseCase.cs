using Zara.Core.Runtime;

namespace Zara.Application.Locking;

/// <summary>
/// Coordinates lock requests with the external overlay projection.
/// </summary>
public interface ILockRuntimeUseCase
{
    /// <summary>
    /// Gets the latest runtime state, including the last confirmed overlay projection.
    /// </summary>
    RuntimeState CurrentState { get; }

    /// <summary>
    /// Requests the default lock and waits for its overlay effect to finish.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending request.</param>
    /// <returns>A task that completes after all effects produced by the request finish.</returns>
    Task RequestLockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests the development safety unlock and waits until all overlays are removed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending request.</param>
    /// <returns>A task that completes after all effects produced by the request finish.</returns>
    Task RequestDevelopmentUnlockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that an adapter can no longer guarantee a previously confirmed overlay projection.
    /// </summary>
    /// <param name="visibility">The projection that the adapter can no longer confirm.</param>
    /// <param name="cancellationToken">Cancels the pending report.</param>
    /// <returns>A task that completes after Core records the projection as unknown.</returns>
    Task ReportOverlayProjectionInvalidatedAsync(
        OverlayVisibility visibility,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes active overlays before the desktop process is allowed to shut down.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending cleanup request.</param>
    /// <returns>A task that completes only after the overlay projection is confirmed hidden.</returns>
    Task PrepareForExitAsync(CancellationToken cancellationToken = default);
}
