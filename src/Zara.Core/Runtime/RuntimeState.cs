namespace Zara.Core.Runtime;

/// <summary>
/// Describes whether the runtime currently requires the default lock.
/// </summary>
public enum LockState
{
    /// <summary>
    /// The runtime does not currently require the lock.
    /// </summary>
    Unlocked,

    /// <summary>
    /// The runtime currently requires the lock.
    /// </summary>
    Locked,
}

/// <summary>
/// Describes the visibility requested from the lock-overlay adapter.
/// </summary>
public enum OverlayVisibility
{
    /// <summary>
    /// No lock overlay should remain visible.
    /// </summary>
    Hidden,

    /// <summary>
    /// Every current display should be covered by a lock overlay.
    /// </summary>
    Visible,
}

/// <summary>
/// Describes the last known state of the external overlay projection.
/// </summary>
/// <remarks>
/// Applying states prevent duplicate requests from producing duplicate effects. <see cref="Unknown"/>
/// records that an adapter operation failed or was cancelled, so the runtime does not claim an
/// external state that it could not verify.
/// </remarks>
public enum OverlayProjectionState
{
    /// <summary>
    /// The adapter could not confirm whether overlays are visible.
    /// </summary>
    Unknown,

    /// <summary>
    /// The adapter confirmed that no overlays remain visible.
    /// </summary>
    Hidden,

    /// <summary>
    /// An operation that makes overlays visible is in progress.
    /// </summary>
    ApplyingVisible,

    /// <summary>
    /// The adapter confirmed that overlays cover the current displays.
    /// </summary>
    Visible,

    /// <summary>
    /// An operation that removes all overlays is in progress.
    /// </summary>
    ApplyingHidden,
}

/// <summary>
/// Holds the runtime's desired lock state separately from the last known external overlay state.
/// </summary>
/// <param name="DesiredLock">The lock state requested by the product runtime.</param>
/// <param name="OverlayProjection">The last known state of the overlay adapter.</param>
public sealed record RuntimeState(
    LockState DesiredLock,
    OverlayProjectionState OverlayProjection)
{
    /// <summary>
    /// Gets the safe initial state before any lock request has been processed.
    /// </summary>
    public static RuntimeState Initial { get; } =
        new(LockState.Unlocked, OverlayProjectionState.Hidden);
}
