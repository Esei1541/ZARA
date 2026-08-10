using System.Collections.ObjectModel;

namespace Zara.Core.UsagePolicy;

/// <summary>
/// Contains every persisted setting used to calculate time-based restrictions and their exceptions.
/// </summary>
public sealed record UsagePolicySettings
{
    /// <summary>
    /// Creates an immutable settings snapshot.
    /// </summary>
    /// <param name="weeklySchedule">The seven weekday rules.</param>
    /// <param name="emergencyUnlock">The emergency-unlock configuration.</param>
    /// <param name="reservations">All registered time-outside-use reservations.</param>
    public UsagePolicySettings(
        WeeklyUsageRestrictionSchedule weeklySchedule,
        EmergencyUnlockSettings emergencyUnlock,
        IEnumerable<OutOfHoursReservation> reservations)
    {
        ArgumentNullException.ThrowIfNull(weeklySchedule);
        ArgumentNullException.ThrowIfNull(emergencyUnlock);
        ArgumentNullException.ThrowIfNull(reservations);

        OutOfHoursReservation[] copiedReservations = reservations.ToArray();
        ValidateReservations(copiedReservations);

        WeeklySchedule = weeklySchedule;
        EmergencyUnlock = emergencyUnlock;
        Reservations = Array.AsReadOnly(copiedReservations);
    }

    /// <summary>
    /// Gets settings with no enabled weekday restrictions, a ten-minute emergency unlock, and no
    /// reservations.
    /// </summary>
    public static UsagePolicySettings Default { get; } = new(
        WeeklyUsageRestrictionSchedule.Default,
        EmergencyUnlockSettings.Default,
        Array.Empty<OutOfHoursReservation>());

    /// <summary>Gets the weekday restriction schedule.</summary>
    public WeeklyUsageRestrictionSchedule WeeklySchedule { get; }

    /// <summary>Gets the emergency-unlock configuration.</summary>
    public EmergencyUnlockSettings EmergencyUnlock { get; }

    /// <summary>Gets the immutable registered-reservation snapshot.</summary>
    public ReadOnlyCollection<OutOfHoursReservation> Reservations { get; }

    /// <summary>
    /// Returns a copy with a different complete weekday schedule.
    /// </summary>
    public UsagePolicySettings WithWeeklySchedule(WeeklyUsageRestrictionSchedule weeklySchedule) =>
        new(weeklySchedule, EmergencyUnlock, Reservations);

    /// <summary>
    /// Returns a copy with a different emergency-unlock configuration.
    /// </summary>
    public UsagePolicySettings WithEmergencyUnlock(EmergencyUnlockSettings emergencyUnlock) =>
        new(WeeklySchedule, emergencyUnlock, Reservations);

    /// <summary>
    /// Returns a copy with a different complete reservation list.
    /// </summary>
    public UsagePolicySettings WithReservations(IEnumerable<OutOfHoursReservation> reservations) =>
        new(WeeklySchedule, EmergencyUnlock, reservations);

    private static void ValidateReservations(OutOfHoursReservation[] reservations)
    {
        var identifiers = new HashSet<Guid>();
        for (int index = 0; index < reservations.Length; index++)
        {
            OutOfHoursReservation reservation = reservations[index] ??
                throw new ArgumentException(
                    "The reservation list cannot contain null values.",
                    nameof(reservations));
            if (!identifiers.Add(reservation.Id))
            {
                throw new ArgumentException(
                    "Reservation identifiers must be unique.",
                    nameof(reservations));
            }

            for (int candidateIndex = 0; candidateIndex < index; candidateIndex++)
            {
                if (UsagePolicyEvaluator.Overlaps(reservations[candidateIndex], reservation))
                {
                    throw new ArgumentException(
                        "Registered reservations must not overlap.",
                        nameof(reservations));
                }
            }
        }
    }
}
