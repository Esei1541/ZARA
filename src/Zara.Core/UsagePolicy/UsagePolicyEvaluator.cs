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
    /// Finds the first local time at or after <paramref name="localNow"/> when the configured
    /// policy actually requires the lock.
    /// </summary>
    /// <remarks>
    /// Weekday restrictions repeat every week. Reservation intervals and the optional current
    /// emergency unlock suppress the lock, so a reservation that covers a restriction start can
    /// move the result to the reservation end or to a later weekly occurrence. When the lock is
    /// already required at <paramref name="localNow"/>, this method returns
    /// <paramref name="localNow"/> itself. Restriction and reservation starts are inclusive, while
    /// their release and end times are exclusive.
    /// </remarks>
    /// <param name="settings">The immutable settings snapshot to inspect.</param>
    /// <param name="localNow">The current local calendar date and time.</param>
    /// <param name="emergencyUnlockEndLocalTime">
    /// The local time when a currently active emergency unlock ends. A value later than
    /// <paramref name="localNow"/> suppresses locking until that instant. A null, equal, or earlier
    /// value means that no current emergency unlock affects this calculation.
    /// </param>
    /// <returns>
    /// The first local lock-required time at or after <paramref name="localNow"/>, or
    /// <see langword="null"/> when every weekday restriction is disabled or no later occurrence
    /// can be represented by <see cref="DateTime"/>.
    /// </returns>
    public static DateTime? FindNextLockStart(
        UsagePolicySettings settings,
        DateTime localNow,
        DateTime? emergencyUnlockEndLocalTime = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DateTime? effectiveEmergencyUnlockEnd = emergencyUnlockEndLocalTime > localNow
            ? emergencyUnlockEndLocalTime
            : null;
        if (IsLockRequiredAt(settings, localNow, effectiveEmergencyUnlockEnd))
        {
            return localNow;
        }

        if (!HasEnabledRestriction(settings.WeeklySchedule))
        {
            return null;
        }

        DateTime[] reservationEndCandidates = settings.Reservations
            .Select(reservation => reservation.Date.ToDateTime(reservation.EndTime, localNow.Kind))
            .Where(candidate => candidate > localNow)
            .OrderBy(candidate => candidate)
            .ToArray();
        int reservationEndIndex = 0;

        DateTime scheduleSearchStart = effectiveEmergencyUnlockEnd ?? localNow;
        DateTime? nextRestrictionStart = FindNextRestrictionStart(
            settings.WeeklySchedule,
            scheduleSearchStart);
        DateTime? emergencyUnlockEndCandidate = effectiveEmergencyUnlockEnd;

        while (true)
        {
            DateTime? reservationEndCandidate =
                reservationEndIndex < reservationEndCandidates.Length
                    ? reservationEndCandidates[reservationEndIndex]
                    : null;
            DateTime? candidate = Earliest(
                nextRestrictionStart,
                reservationEndCandidate,
                emergencyUnlockEndCandidate);
            if (candidate is null)
            {
                return null;
            }

            if (IsLockRequiredAt(settings, candidate.Value, effectiveEmergencyUnlockEnd))
            {
                return candidate;
            }

            if (nextRestrictionStart == candidate)
            {
                nextRestrictionStart = FindNextRestrictionStart(
                    settings.WeeklySchedule,
                    candidate.Value);
            }

            while (reservationEndIndex < reservationEndCandidates.Length &&
                   reservationEndCandidates[reservationEndIndex] == candidate)
            {
                reservationEndIndex++;
            }

            if (emergencyUnlockEndCandidate == candidate)
            {
                emergencyUnlockEndCandidate = null;
            }
        }
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
    /// Removes a registered reservation regardless of its interval or the current restriction.
    /// </summary>
    /// <param name="settings">The current immutable settings snapshot.</param>
    /// <param name="reservationId">The stable identifier of the reservation to remove.</param>
    /// <returns>The changed snapshot or the unchanged snapshot with the rejection status.</returns>
    public static ReservationChangeResult TryRemoveReservation(
        UsagePolicySettings settings,
        Guid reservationId)
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

    private static DateTime? Earliest(
        DateTime? first,
        DateTime? second,
        DateTime? third)
    {
        DateTime? earliest = first;
        if (second is not null && (earliest is null || second < earliest))
        {
            earliest = second;
        }

        if (third is not null && (earliest is null || third < earliest))
        {
            earliest = third;
        }

        return earliest;
    }

    private static DateTime? FindNextRestrictionStart(
        WeeklyUsageRestrictionSchedule schedule,
        DateTime after)
    {
        DateTime firstDate = after.Date;
        for (int dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            long candidateDateTicks = firstDate.Ticks + (TimeSpan.TicksPerDay * dayOffset);
            if (candidateDateTicks > DateTime.MaxValue.Date.Ticks)
            {
                break;
            }

            var candidateDate = new DateTime(candidateDateTicks, after.Kind);
            DailyUsageRestriction restriction = schedule.GetRestriction(candidateDate.DayOfWeek);
            if (!restriction.IsEnabled)
            {
                continue;
            }

            DateTime candidate = candidateDate.Add(restriction.StartTime.ToTimeSpan());
            if (candidate > after)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasEnabledRestriction(WeeklyUsageRestrictionSchedule schedule)
    {
        for (int dayValue = 0; dayValue < 7; dayValue++)
        {
            if (schedule.GetRestriction((DayOfWeek)dayValue).IsEnabled)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLockRequiredAt(
        UsagePolicySettings settings,
        DateTime localTime,
        DateTime? emergencyUnlockEndLocalTime) =>
        Evaluate(
            settings,
            localTime,
            emergencyUnlockEndLocalTime is DateTime unlockEnd && localTime < unlockEnd)
        .LockRequired;

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
