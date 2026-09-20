using Zara.Core.Runtime;

namespace Zara.Application.Locking;

/// <summary>
/// Identifies the latest requested lock intent independently of adapter projection results.
/// </summary>
/// <param name="DesiredLock">The lock state requested by the latest intent.</param>
/// <param name="Revision">
/// A monotonically increasing value changed by every explicit lock, unlock, safety-unlock, or exit intent.
/// </param>
public sealed record LockIntentSnapshot(LockState DesiredLock, long Revision);

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
    /// Gets an atomic snapshot of the latest requested lock intent and its revision.
    /// </summary>
    LockIntentSnapshot CurrentIntent { get; }

    /// <summary>
    /// Requests the default lock. During recovery, repeated requests preserve the retry deadline.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending request.</param>
    /// <returns>
    /// A task that completes after this attempt finishes or an existing recovery accepts the intent.
    /// The current state distinguishes a confirmed projection from effects still awaiting recovery.
    /// </returns>
    Task RequestLockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests that the normal lock be removed and waits until all overlays are removed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending request.</param>
    /// <returns>A task that completes after all effects produced by the request finish.</returns>
    Task RequestUnlockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests the development safety unlock and waits until all overlays are removed.
    /// This remains separate from product policy so development recovery keeps its unconditional
    /// behavior even when the product's time rules would ordinarily require a lock.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pending request.</param>
    /// <returns>A task that completes after all effects produced by the request finish.</returns>
    Task RequestDevelopmentUnlockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the lock only when no newer intent has replaced the expected revision.
    /// </summary>
    /// <param name="expectedIntentRevision">
    /// The exact intent revision that must still be current before restoration begins.
    /// </param>
    /// <param name="cancellationToken">Cancels the pending conditional request.</param>
    /// <returns>
    /// <see langword="true"/> when the restoration intent was accepted; the current state indicates
    /// whether its effects completed or are awaiting an existing recovery;
    /// <see langword="false"/> when a newer intent made the restoration obsolete.
    /// </returns>
    Task<bool> RestoreLockIfIntentRevisionAsync(
        long expectedIntentRevision,
        CancellationToken cancellationToken = default);

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
