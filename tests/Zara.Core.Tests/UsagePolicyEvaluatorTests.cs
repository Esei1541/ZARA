using Zara.Core.UsagePolicy;

namespace Zara.Core.Tests;

[TestClass]
public sealed class UsagePolicyEvaluatorTests
{
    [TestMethod]
    public void DefaultSettingsDisableEveryWeekdayAndUseConfirmedEmergencyDefaults()
    {
        UsagePolicySettings settings = UsagePolicySettings.Default;

        foreach (DayOfWeek dayOfWeek in Enum.GetValues<DayOfWeek>())
        {
            Assert.IsFalse(settings.WeeklySchedule.GetRestriction(dayOfWeek).IsEnabled);
        }

        Assert.AreEqual(10, settings.EmergencyUnlock.DurationMinutes);
        Assert.AreEqual(3, settings.EmergencyUnlock.SentenceCount);
        Assert.IsEmpty(settings.Reservations);
    }

    [TestMethod]
    public void EnabledRestrictionRejectsEqualStartAndReleaseTimes()
    {
        _ = Assert.ThrowsExactly<ArgumentException>(() => new DailyUsageRestriction(
            isEnabled: true,
            startTime: new TimeOnly(9, 0),
            releaseTime: new TimeOnly(9, 0)));
    }

    [TestMethod]
    public void NormalRestrictionIncludesStartButExcludesRelease()
    {
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(18, 0)));

        UsagePolicyEvaluation atStart = UsagePolicyEvaluator.Evaluate(
            settings,
            LocalTime(2026, 8, 10, 9, 0),
            isEmergencyUnlockActive: false);
        UsagePolicyEvaluation beforeRelease = UsagePolicyEvaluator.Evaluate(
            settings,
            LocalTime(2026, 8, 10, 17, 59),
            isEmergencyUnlockActive: false);
        UsagePolicyEvaluation atRelease = UsagePolicyEvaluator.Evaluate(
            settings,
            LocalTime(2026, 8, 10, 18, 0),
            isEmergencyUnlockActive: false);

        Assert.IsTrue(atStart.LockRequired);
        Assert.IsTrue(beforeRelease.LockRequired);
        Assert.IsFalse(atRelease.LockRequired);
        Assert.IsTrue(atStart.IsWithinUsageBan);
        Assert.IsFalse(atStart.IsSettingsChangeAllowed);
    }

    [TestMethod]
    public void OvernightRestrictionContinuesUntilTheFollowingDayReleaseTime()
    {
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(22, 0), new TimeOnly(6, 0)));

        UsagePolicyEvaluation mondayEvening = UsagePolicyEvaluator.Evaluate(
            settings,
            LocalTime(2026, 8, 10, 22, 0),
            isEmergencyUnlockActive: false);
        UsagePolicyEvaluation tuesdayBeforeRelease = UsagePolicyEvaluator.Evaluate(
            settings,
            LocalTime(2026, 8, 11, 5, 59),
            isEmergencyUnlockActive: false);
        UsagePolicyEvaluation tuesdayAtRelease = UsagePolicyEvaluator.Evaluate(
            settings,
            LocalTime(2026, 8, 11, 6, 0),
            isEmergencyUnlockActive: false);

        Assert.IsTrue(mondayEvening.LockRequired);
        Assert.IsTrue(tuesdayBeforeRelease.LockRequired);
        Assert.IsFalse(tuesdayAtRelease.LockRequired);
    }

    [TestMethod]
    public void OvernightSundayRestrictionContinuesThroughMonday()
    {
        var sundayRestriction = new DailyUsageRestriction(
            true,
            new TimeOnly(22, 0),
            new TimeOnly(6, 0));
        var settings = new UsagePolicySettings(
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(DayOfWeek.Sunday, sundayRestriction),
            EmergencyUnlockSettings.Default,
            Array.Empty<OutOfHoursReservation>());

        UsagePolicyEvaluation mondayBeforeRelease = UsagePolicyEvaluator.Evaluate(
            settings,
            LocalTime(2026, 8, 10, 5, 59),
            isEmergencyUnlockActive: false);
        UsagePolicyEvaluation mondayAtRelease = UsagePolicyEvaluator.Evaluate(
            settings,
            LocalTime(2026, 8, 10, 6, 0),
            isEmergencyUnlockActive: false);

        Assert.IsTrue(mondayBeforeRelease.LockRequired);
        Assert.IsFalse(mondayAtRelease.LockRequired);
    }

    [TestMethod]
    public void FindNextLockStartReturnsNullWhenEveryWeekdayIsDisabled()
    {
        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(
            UsagePolicySettings.Default,
            LocalTime(2026, 8, 10, 8, 0));

        Assert.IsNull(nextLockStart);
    }

    [TestMethod]
    public void FindNextLockStartReturnsCurrentTimeWhenLockIsAlreadyRequired()
    {
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(18, 0)));
        DateTime localNow = LocalTime(2026, 8, 10, 10, 30);

        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(settings, localNow);

        Assert.AreEqual(localNow, nextLockStart);
    }

    [TestMethod]
    public void FindNextLockStartFindsTheNextEnabledWeekday()
    {
        var tuesdayRestriction = new DailyUsageRestriction(
            true,
            new TimeOnly(9, 15),
            new TimeOnly(10, 0));
        UsagePolicySettings settings = SettingsWithRestriction(
            DayOfWeek.Tuesday,
            tuesdayRestriction);

        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(
            settings,
            LocalTime(2026, 8, 10, 20, 0));

        Assert.AreEqual(LocalTime(2026, 8, 11, 9, 15), nextLockStart);
    }

    [TestMethod]
    public void FindNextLockStartRepeatsAnOvernightRestrictionAfterItsReleaseBoundary()
    {
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(22, 0), new TimeOnly(6, 0)));

        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(
            settings,
            LocalTime(2026, 8, 11, 6, 0));

        Assert.AreEqual(LocalTime(2026, 8, 17, 22, 0), nextLockStart);
    }

    [TestMethod]
    public void FindNextLockStartMovesToReservationEndInsideTheRestriction()
    {
        OutOfHoursReservation reservation = Reservation(
            new DateOnly(2026, 8, 10),
            new TimeOnly(8, 0),
            new TimeOnly(10, 0));
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(18, 0)),
            reservation);

        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(
            settings,
            LocalTime(2026, 8, 10, 8, 0));

        Assert.AreEqual(LocalTime(2026, 8, 10, 10, 0), nextLockStart);
    }

    [TestMethod]
    public void FindNextLockStartSkipsARestrictionFullyCoveredByAReservation()
    {
        OutOfHoursReservation reservation = Reservation(
            new DateOnly(2026, 8, 10),
            new TimeOnly(8, 0),
            new TimeOnly(18, 0));
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(18, 0)),
            reservation);

        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(
            settings,
            LocalTime(2026, 8, 10, 8, 0));

        Assert.AreEqual(LocalTime(2026, 8, 17, 9, 0), nextLockStart);
    }

    [TestMethod]
    public void FindNextLockStartPassesAcrossAdjacentReservations()
    {
        OutOfHoursReservation firstReservation = Reservation(
            new DateOnly(2026, 8, 10),
            new TimeOnly(8, 0),
            new TimeOnly(10, 0));
        OutOfHoursReservation secondReservation = Reservation(
            new DateOnly(2026, 8, 10),
            new TimeOnly(10, 0),
            new TimeOnly(11, 0));
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(18, 0)),
            firstReservation,
            secondReservation);

        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(
            settings,
            LocalTime(2026, 8, 10, 8, 0));

        Assert.AreEqual(LocalTime(2026, 8, 10, 11, 0), nextLockStart);
    }

    [TestMethod]
    public void FindNextLockStartUsesReservationEndDuringAnOvernightRestriction()
    {
        OutOfHoursReservation reservation = Reservation(
            new DateOnly(2026, 8, 11),
            new TimeOnly(0, 0),
            new TimeOnly(2, 0));
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(22, 0), new TimeOnly(6, 0)),
            reservation);

        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(
            settings,
            LocalTime(2026, 8, 11, 1, 0));

        Assert.AreEqual(LocalTime(2026, 8, 11, 2, 0), nextLockStart);
    }

    [TestMethod]
    public void FindNextLockStartUsesEmergencyUnlockEndInsideTheRestriction()
    {
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(18, 0)));

        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(
            settings,
            LocalTime(2026, 8, 10, 10, 0),
            emergencyUnlockEndLocalTime: LocalTime(2026, 8, 10, 11, 0));

        Assert.AreEqual(LocalTime(2026, 8, 10, 11, 0), nextLockStart);
    }

    [TestMethod]
    public void FindNextLockStartSkipsARestrictionCoveredByEmergencyUnlock()
    {
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(18, 0)));

        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(
            settings,
            LocalTime(2026, 8, 10, 10, 0),
            emergencyUnlockEndLocalTime: LocalTime(2026, 8, 10, 19, 0));

        Assert.AreEqual(LocalTime(2026, 8, 17, 9, 0), nextLockStart);
    }

    [TestMethod]
    public void FindNextLockStartWaitsForReservationWhenEmergencyUnlockEndsFirst()
    {
        OutOfHoursReservation reservation = Reservation(
            new DateOnly(2026, 8, 10),
            new TimeOnly(9, 30),
            new TimeOnly(11, 0));
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(18, 0)),
            reservation);

        DateTime? nextLockStart = UsagePolicyEvaluator.FindNextLockStart(
            settings,
            LocalTime(2026, 8, 10, 9, 45),
            emergencyUnlockEndLocalTime: LocalTime(2026, 8, 10, 10, 0));

        Assert.AreEqual(LocalTime(2026, 8, 10, 11, 0), nextLockStart);
    }

    [TestMethod]
    public void ReservationAndEmergencyUnlockReleaseTheLockButNotSettingChanges()
    {
        Guid reservationId = Guid.NewGuid();
        var reservation = new OutOfHoursReservation(
            reservationId,
            new DateOnly(2026, 8, 10),
            new TimeOnly(10, 0),
            new TimeOnly(11, 0),
            "업무");
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(18, 0)),
            reservation);

        UsagePolicyEvaluation reservationEvaluation = UsagePolicyEvaluator.Evaluate(
            settings,
            LocalTime(2026, 8, 10, 10, 30),
            isEmergencyUnlockActive: false);
        UsagePolicyEvaluation emergencyEvaluation = UsagePolicyEvaluator.Evaluate(
            settings,
            LocalTime(2026, 8, 10, 12, 0),
            isEmergencyUnlockActive: true);

        Assert.IsTrue(reservationEvaluation.IsWithinUsageBan);
        Assert.IsTrue(reservationEvaluation.HasActiveReservation);
        Assert.IsFalse(reservationEvaluation.LockRequired);
        Assert.IsFalse(reservationEvaluation.IsSettingsChangeAllowed);
        Assert.IsTrue(emergencyEvaluation.IsWithinUsageBan);
        Assert.IsTrue(emergencyEvaluation.HasActiveEmergencyUnlock);
        Assert.IsFalse(emergencyEvaluation.LockRequired);
        Assert.IsTrue(emergencyEvaluation.LockRequiredAfterRestart);
        Assert.IsFalse(reservationEvaluation.LockRequiredAfterRestart);
        Assert.IsFalse(emergencyEvaluation.IsSettingsChangeAllowed);
    }

    [TestMethod]
    public void EmergencySettingsEnforceConfirmedInclusiveRanges()
    {
        _ = new EmergencyUnlockSettings(1, 0);
        _ = new EmergencyUnlockSettings(60, 99);

        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new EmergencyUnlockSettings(0, 3));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new EmergencyUnlockSettings(10, 100));
    }

    [TestMethod]
    public void TryAddReservationRejectsAnOverlappingInterval()
    {
        var existing = Reservation(
            new DateOnly(2026, 9, 10),
            new TimeOnly(0, 0),
            new TimeOnly(2, 0));
        UsagePolicySettings settings = SettingsWithReservations(existing);
        OutOfHoursReservation candidate = Reservation(
            new DateOnly(2026, 9, 10),
            new TimeOnly(1, 30),
            new TimeOnly(3, 0));

        ReservationChangeResult result = UsagePolicyEvaluator.TryAddReservation(settings, candidate);

        Assert.AreEqual(ReservationChangeStatus.ConflictsWithExisting, result.Status);
        Assert.IsFalse(result.Succeeded);
        Assert.AreSame(settings, result.Settings);
    }

    [TestMethod]
    public void TryAddReservationAcceptsAnAdjacentInterval()
    {
        var existing = Reservation(
            new DateOnly(2026, 9, 10),
            new TimeOnly(0, 0),
            new TimeOnly(2, 0));
        UsagePolicySettings settings = SettingsWithReservations(existing);
        OutOfHoursReservation candidate = Reservation(
            new DateOnly(2026, 9, 10),
            new TimeOnly(2, 0),
            new TimeOnly(3, 0));

        ReservationChangeResult result = UsagePolicyEvaluator.TryAddReservation(settings, candidate);

        Assert.AreEqual(ReservationChangeStatus.Added, result.Status);
        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(2, result.Settings.Reservations);
    }

    [TestMethod]
    public void TryAddReservationAllowsAnIntervalOutsideCurrentRestrictionTime()
    {
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(22, 0), new TimeOnly(6, 0)));
        OutOfHoursReservation reservation = Reservation(
            new DateOnly(2026, 8, 10),
            new TimeOnly(10, 0),
            new TimeOnly(11, 0));

        ReservationChangeResult result = UsagePolicyEvaluator.TryAddReservation(settings, reservation);

        Assert.AreEqual(ReservationChangeStatus.Added, result.Status);
        Assert.HasCount(1, result.Settings.Reservations);
    }

    [TestMethod]
    public void TryRemoveReservationRemovesOnlyTheRequestedReservation()
    {
        OutOfHoursReservation reservation = Reservation(
            new DateOnly(2026, 8, 10), new TimeOnly(10, 0), new TimeOnly(11, 0));
        OutOfHoursReservation other = Reservation(
            new DateOnly(2026, 8, 11), new TimeOnly(10, 0), new TimeOnly(11, 0));
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(12, 0)),
            reservation,
            other);

        ReservationChangeResult result = UsagePolicyEvaluator.TryRemoveReservation(settings, reservation.Id);

        Assert.AreEqual(ReservationChangeStatus.Removed, result.Status);
        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(new[] { other }, result.Settings.Reservations.ToArray());
        Assert.HasCount(2, settings.Reservations);
        Assert.AreEqual(settings.WeeklySchedule, result.Settings.WeeklySchedule);
        Assert.AreEqual(settings.EmergencyUnlock, result.Settings.EmergencyUnlock);
    }

    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    public void ExpiredReservationCleanupUsesTheExactLocalEndTime(int tickOffset, bool expired)
    {
        var reservation = Reservation(
            new DateOnly(2026, 9, 22), new TimeOnly(0, 0), new TimeOnly(1, 0));
        UsagePolicySettings settings = SettingsWithReservations(reservation);
        DateTime localNow = new DateTime(2026, 9, 22, 1, 0, 0).AddTicks(tickOffset);

        UsagePolicySettings remaining = UsagePolicyEvaluator.RemoveExpiredReservations(settings, localNow);

        Assert.HasCount(expired ? 0 : 1, remaining.Reservations);
        Assert.HasCount(1, settings.Reservations);
        Assert.AreSame(settings.WeeklySchedule, remaining.WeeklySchedule);
        Assert.AreSame(settings.EmergencyUnlock, remaining.EmergencyUnlock);
        if (!expired)
        {
            Assert.AreSame(settings, remaining);
        }
    }

    [TestMethod]
    public void ExpiredReservationCleanupPreservesActiveAndFutureReservations()
    {
        var past = Reservation(new DateOnly(2026, 9, 21), new TimeOnly(23, 0), new TimeOnly(23, 59));
        var ended = Reservation(new DateOnly(2026, 9, 22), new TimeOnly(0, 0), new TimeOnly(1, 0));
        var active = Reservation(new DateOnly(2026, 9, 22), new TimeOnly(14, 0), new TimeOnly(16, 0));
        var later = Reservation(new DateOnly(2026, 9, 22), new TimeOnly(16, 0), new TimeOnly(17, 0));
        var tomorrow = Reservation(new DateOnly(2026, 9, 23), new TimeOnly(0, 0), new TimeOnly(1, 0));
        UsagePolicySettings settings = SettingsWithReservations(past, ended, active, later, tomorrow);

        UsagePolicySettings remaining = UsagePolicyEvaluator.RemoveExpiredReservations(
            settings, new DateTime(2026, 9, 22, 15, 0, 0));

        CollectionAssert.AreEqual(new[] { active, later, tomorrow }, remaining.Reservations.ToArray());
        Assert.AreSame(remaining, UsagePolicyEvaluator.RemoveExpiredReservations(
            remaining, new DateTime(2026, 9, 22, 15, 0, 1)));
    }

    [TestMethod]
    public void TryRemoveReservationLeavesUnknownIdsUntouched()
    {
        OutOfHoursReservation reservation = Reservation(
            new DateOnly(2026, 8, 10),
            new TimeOnly(10, 0),
            new TimeOnly(11, 0));
        UsagePolicySettings settings = SettingsWithReservations(reservation);

        ReservationChangeResult removed = UsagePolicyEvaluator.TryRemoveReservation(
            settings,
            reservation.Id);
        ReservationChangeResult missing = UsagePolicyEvaluator.TryRemoveReservation(
            removed.Settings,
            Guid.NewGuid());

        Assert.AreEqual(ReservationChangeStatus.Removed, removed.Status);
        Assert.IsTrue(removed.Succeeded);
        Assert.IsEmpty(removed.Settings.Reservations);
        Assert.AreEqual(ReservationChangeStatus.NotFound, missing.Status);
        Assert.AreSame(removed.Settings, missing.Settings);
    }

    private static UsagePolicySettings SettingsWithMondayRestriction(
        DailyUsageRestriction monday,
        params OutOfHoursReservation[] reservations) =>
        SettingsWithRestriction(DayOfWeek.Monday, monday, reservations);

    private static UsagePolicySettings SettingsWithRestriction(
        DayOfWeek dayOfWeek,
        DailyUsageRestriction restriction,
        params OutOfHoursReservation[] reservations) =>
        new(
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(dayOfWeek, restriction),
            EmergencyUnlockSettings.Default,
            reservations);

    private static UsagePolicySettings SettingsWithReservations(
        params OutOfHoursReservation[] reservations) =>
        new(
            WeeklyUsageRestrictionSchedule.Default,
            EmergencyUnlockSettings.Default,
            reservations);

    private static OutOfHoursReservation Reservation(
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime) =>
        new(Guid.NewGuid(), date, startTime, endTime, "메모");

    private static DateTime LocalTime(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, second: 0, DateTimeKind.Unspecified);
}
