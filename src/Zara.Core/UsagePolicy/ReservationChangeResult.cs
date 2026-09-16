namespace Zara.Core.UsagePolicy;

/// <summary>Identifies the outcome of a pure reservation add or delete request.</summary>
public enum ReservationChangeStatus
{
    /// <summary>The reservation was added.</summary>
    Added,

    /// <summary>The reservation was removed.</summary>
    Removed,

    /// <summary>The requested interval overlaps a registered reservation.</summary>
    ConflictsWithExisting,

    /// <summary>No reservation has the requested identifier.</summary>
    NotFound,
}

/// <summary>
/// Returns a new settings snapshot when a reservation mutation succeeds, otherwise the unchanged
/// input snapshot.
/// </summary>
/// <param name="Status">The add or delete outcome.</param>
/// <param name="Settings">The resulting immutable settings snapshot.</param>
public sealed record ReservationChangeResult(
    ReservationChangeStatus Status,
    UsagePolicySettings Settings)
{
    /// <summary>Gets whether the request changed the reservation list.</summary>
    public bool Succeeded => Status is ReservationChangeStatus.Added or ReservationChangeStatus.Removed;
}
