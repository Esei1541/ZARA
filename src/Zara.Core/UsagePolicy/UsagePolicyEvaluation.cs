namespace Zara.Core.UsagePolicy;

/// <summary>
/// Describes the independently calculated time-rule inputs and their resulting lock decision.
/// </summary>
/// <param name="IsWithinUsageBan">
/// Whether the current local time is inside a configured weekday restriction.
/// </param>
/// <param name="HasActiveReservation">
/// Whether the current local time is covered by a registered reservation.
/// </param>
/// <param name="HasActiveEmergencyUnlock">
/// Whether the caller has an active in-memory emergency unlock.
/// </param>
/// <param name="LockRequired">Whether the default lock must currently be projected.</param>
/// <param name="IsSettingsChangeAllowed">
/// Whether setting changes are allowed at the current base restriction time.
/// </param>
public sealed record UsagePolicyEvaluation(
    bool IsWithinUsageBan,
    bool HasActiveReservation,
    bool HasActiveEmergencyUnlock,
    bool LockRequired,
    bool IsSettingsChangeAllowed);
