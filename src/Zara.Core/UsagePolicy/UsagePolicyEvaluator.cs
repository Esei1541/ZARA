namespace Zara.Core.UsagePolicy;

/// <summary>
/// Evaluates weekday restrictions and reservations without reading Windows settings, persistence,
/// or UI state.
/// </summary>
public static class UsagePolicyEvaluator
{
    /// <summary>
    /// Calculates the current restriction and final lock decision from a caller-supplied local time.
    /// </summary>
    /// <remarks>
    /// The caller supplies the local Windows time. This type deliberately does not apply a separate
    /// time-zone or daylight-saving policy. A reservation and emergency unlock affect only the
    /// lock decision; setting changes remain unavailable during the base restricted period.
    /// </remarks>
    /// <param name="settings">The immutable settings snapshot to evaluate.</param>
    /// <param name="localNow">The current local calendar date and time.</param>
    /// <param name="isEmergencyUnlockActive">
    /// Whether an in-memory timed emergency unlock is currently active.
    /// </param>
    /// <returns>The independently calculated inputs and final lock decision.</returns>
    public static UsagePolicyEvaluation Evaluate(
        UsagePolicySettings settings,
        DateTime localNow,
        bool isEmergencyUnlockActive)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool isBaseUsageRestrictionActive = IsBaseUsageRestrictionActive(
            settings.WeeklySchedule,
            localNow);
        bool hasActiveOutOfHoursReservation = settings.Reservations.Any(
            reservation => Contains(reservation, localNow));
        bool isLockRequired = isBaseUsageRestrictionActive &&
            !hasActiveOutOfHoursReservation &&
            !isEmergencyUnlockActive;

        return new UsagePolicyEvaluation(
            IsWithinUsageBan: isBaseUsageRestrictionActive,
            HasActiveReservation: hasActiveOutOfHoursReservation,
            HasActiveEmergencyUnlock: isEmergencyUnlockActive,
            LockRequired: isLockRequired,
            IsSettingsChangeAllowed: !isBaseUsageRestrictionActive);
    }

    /// <summary>
    /// Adds a non-overlapping reservation without considering whether the reservation falls inside
    /// a currently configured weekday restriction.
    /// </summary>
    /// <param name="settings">The current immutable settings snapshot.</param>
    /// <param name="reservation">The same-day reservation to add.</param>
    /// <returns>The changed snapshot or the unchanged snapshot with a conflict status.</returns>
    public static ReservationChangeResult TryAddReservation(
        UsagePolicySettings settings,
        OutOfHoursReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(reservation);

        if (settings.Reservations.Any(existing =>
                existing.Id == reservation.Id || Overlaps(existing, reservation)))
        {
            return new ReservationChangeResult(
                ReservationChangeStatus.ConflictsWithExisting,
                settings);
        }

        return new ReservationChangeResult(
            ReservationChangeStatus.Added,
            settings.WithReservations(settings.Reservations.Append(reservation)));
    }

    /// <summary>
    /// Removes a reservation unless it is active at the supplied local time.
    /// </summary>
    /// <param name="settings">The current immutable settings snapshot.</param>
    /// <param name="reservationId">The stable identifier of the reservation to remove.</param>
    /// <param name="localNow">The caller-supplied local time used to check active status.</param>
    /// <returns>The changed snapshot or the unchanged snapshot with the rejection status.</returns>
    public static ReservationChangeResult TryRemoveReservation(
        UsagePolicySettings settings,
        Guid reservationId,
        DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (reservationId == Guid.Empty)
        {
            throw new ArgumentException("A reservation identifier is required.", nameof(reservationId));
        }

        OutOfHoursReservation? reservation = settings.Reservations.FirstOrDefault(
            candidate => candidate.Id == reservationId);
        if (reservation is null)
        {
            return new ReservationChangeResult(ReservationChangeStatus.NotFound, settings);
        }

        if (Contains(reservation, localNow))
        {
            return new ReservationChangeResult(
                ReservationChangeStatus.ActiveReservationCannotBeRemoved,
                settings);
        }

        return new ReservationChangeResult(
            ReservationChangeStatus.Removed,
            settings.WithReservations(settings.Reservations.Where(
                candidate => candidate.Id != reservationId)));
    }

    /// <summary>
    /// Determines whether two same-day reservations share at least one included instant.
    /// </summary>
    /// <remarks>
    /// Intervals use an inclusive start and exclusive end, so adjacent reservations do not overlap.
    /// </remarks>
    internal static bool Overlaps(OutOfHoursReservation left, OutOfHoursReservation right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.Date == right.Date &&
            left.StartTime < right.EndTime &&
            right.StartTime < left.EndTime;
    }

    private static bool IsBaseUsageRestrictionActive(
        WeeklyUsageRestrictionSchedule schedule,
        DateTime localNow)
    {
        TimeOnly currentTime = TimeOnly.FromDateTime(localNow);
        DailyUsageRestriction today = schedule.GetRestriction(localNow.DayOfWeek);
        if (today.IsEnabled &&
            (today.StartTime < today.ReleaseTime
                ? currentTime >= today.StartTime && currentTime < today.ReleaseTime
                : currentTime >= today.StartTime))
        {
            return true;
        }

        DayOfWeek previousDay = (DayOfWeek)(((int)localNow.DayOfWeek + 6) % 7);
        DailyUsageRestriction previous = schedule.GetRestriction(previousDay);
        return previous.IsEnabled &&
            previous.StartTime > previous.ReleaseTime &&
            currentTime < previous.ReleaseTime;
    }

    private static bool Contains(OutOfHoursReservation reservation, DateTime localNow) =>
        reservation.Date == DateOnly.FromDateTime(localNow) &&
        TimeOnly.FromDateTime(localNow) >= reservation.StartTime &&
        TimeOnly.FromDateTime(localNow) < reservation.EndTime;
}
