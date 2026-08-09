namespace Zara.Core.Runtime;

/// <summary>
/// Represents an input to the pure runtime reducer.
/// </summary>
public abstract record RuntimeEvent;

/// <summary>
/// Requests that the runtime enter the default lock.
/// </summary>
public sealed record LockRequested : RuntimeEvent;

/// <summary>
/// Requests a safety unlock that bypasses schedules and other product settings.
/// </summary>
/// <remarks>
/// This event supports development recovery and shutdown cleanup. It is not the product's timed
/// emergency-unlock workflow and does not introduce a separate product mode.
/// </remarks>
public sealed record SafetyUnlockRequested : RuntimeEvent;

/// <summary>
/// Reports that the overlay adapter successfully applied a requested visibility.
/// </summary>
/// <param name="Visibility">The visibility confirmed by the adapter.</param>
public sealed record OverlayProjectionSucceeded(
    OverlayVisibility Visibility) : RuntimeEvent;

/// <summary>
/// Reports that the overlay adapter could not confirm a requested visibility.
/// </summary>
/// <param name="Visibility">The visibility that the adapter attempted to apply.</param>
public sealed record OverlayProjectionFailed(
    OverlayVisibility Visibility) : RuntimeEvent;

/// <summary>
/// Reports that a previously confirmed overlay projection can no longer be guaranteed.
/// </summary>
/// <param name="Visibility">The projection that the adapter can no longer confirm.</param>
/// <remarks>
/// This event records a topology-driven loss of confidence without choosing an automatic retry or
/// deciding whether a partially projected lock should continue.
/// </remarks>
public sealed record OverlayProjectionInvalidated(
    OverlayVisibility Visibility) : RuntimeEvent;
