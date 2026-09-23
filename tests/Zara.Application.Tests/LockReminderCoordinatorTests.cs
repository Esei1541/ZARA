using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;

namespace Zara.Application.Tests;

[TestClass]
public sealed class LockReminderCoordinatorTests
{
    [TestMethod]
    public void EvaluateReturnsUserExampleRemindersBeforeReservationEndLock()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 21, 23, 59, 59, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 22),
            new TimeOnly(0, 0),
            new TimeOnly(0, 30),
            "새벽 사용");
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 0),
            new TimeOnly(3, 0),
            reservations: [reservation]);

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));

        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero), 30);
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 20, 0, TimeSpan.Zero), 10);
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 25, 0, TimeSpan.Zero), 5);
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 29, 0, TimeSpan.Zero), 1);
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 30, 0, TimeSpan.Zero), null);
    }

    [TestMethod]
    public void EvaluateDoesNotTreatReservationEndAsLockWhenRestrictionAlsoEnds()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 2, 54, 59, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 22),
            new TimeOnly(2, 0),
            new TimeOnly(3, 0),
            "끝까지 사용");
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 0),
            new TimeOnly(3, 0),
            reservations: [reservation]);

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 22, 2, 55, 0, TimeSpan.Zero));
        UsagePolicyRuntimeSnapshot snapshot = CreateSnapshot(settings, timeProvider);

        Assert.AreNotEqual(new DateTime(2026, 9, 22, 3, 0, 0), snapshot.NextLockStartLocalTime);
        Assert.IsNull(coordinator.Evaluate(snapshot, LockReminderSettings.Default));
    }

    [TestMethod]
    public void EvaluateUsesTheEndOfAdjacentReservationsAsTheActualLockTarget()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 21, 23, 59, 59, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        var first = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 22),
            new TimeOnly(0, 0),
            new TimeOnly(0, 30),
            "첫 예약");
        var second = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 22),
            new TimeOnly(0, 30),
            new TimeOnly(1, 0),
            "연속 예약");
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 0),
            new TimeOnly(3, 0),
            reservations: [first, second]);

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 30, 0, TimeSpan.Zero), 30);
    }

    [TestMethod]
    public void EvaluateUsesEmergencyUnlockEndAsTheActualLockTarget()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 0),
            new TimeOnly(3, 0));

        Assert.AreEqual(30, coordinator.Evaluate(
            CreateSnapshot(settings, timeProvider, new DateTime(2026, 9, 22, 0, 30, 0)),
            LockReminderSettings.Default));
        AssertReminderAt(
            coordinator,
            timeProvider,
            settings,
            new DateTimeOffset(2026, 9, 22, 0, 20, 0, TimeSpan.Zero),
            10,
            emergencyUnlockEndLocalTime: new DateTime(2026, 9, 22, 0, 30, 0));
    }

    [TestMethod]
    public void EvaluatePlaysReminderWhenANewEmergencyTargetStartsExactlyAtALeadTime()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 0, 20, 0, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 0),
            new TimeOnly(3, 0));

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));

        Assert.AreEqual(
            10,
            coordinator.Evaluate(
                CreateSnapshot(settings, timeProvider, new DateTime(2026, 9, 22, 0, 30, 0)),
                LockReminderSettings.Default));
    }

    [TestMethod]
    public void EvaluateDoesNotReplayReminderWhenStartedAfterAThreshold()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 0, 0, 10, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 22),
            new TimeOnly(0, 0),
            new TimeOnly(0, 30),
            "새벽 사용");
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 0),
            new TimeOnly(3, 0),
            reservations: [reservation]);

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 20, 0, TimeSpan.Zero), 10);
    }

    [TestMethod]
    public void ResetObservationDoesNotReplayAlreadyPlayedReminderForSameLock()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 0, 19, 59, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 30),
            new TimeOnly(3, 0));

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 20, 0, TimeSpan.Zero), 10);

        coordinator.ResetObservation();
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 19, 0, TimeSpan.Zero), null);
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 20, 0, TimeSpan.Zero), null);
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 0, 25, 0, TimeSpan.Zero), 5);
    }

    [TestMethod]
    public void EvaluateSkipsRemindersThatAreTooLateAfterALongDelay()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 11, 49, 0, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(12, 0),
            new TimeOnly(13, 0));

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 22, 11, 59, 30, TimeSpan.Zero));
        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));
    }

    [TestMethod]
    public void EvaluateAllowsSmallPollingDelayForTheClosestCrossedReminder()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 11, 49, 0, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(12, 0),
            new TimeOnly(13, 0));

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 11, 59, 5, TimeSpan.Zero), 1);
    }

    [TestMethod]
    public void EvaluateDoesNotPlayOlderEnabledReminderWhenClosestCrossedReminderIsDisabled()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 11, 49, 0, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(12, 0),
            new TimeOnly(13, 0));
        LockReminderSettings reminderSettings = LockReminderSettings.Default.WithEnabled(1, false);

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), reminderSettings));
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 11, 59, 5, TimeSpan.Zero), null, reminderSettings);
    }

    [TestMethod]
    public void EvaluateStartsFreshWhenTargetChangesWithoutPlayingPastThresholds()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 0, 0, 1, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings firstSettings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 30),
            new TimeOnly(3, 0));
        UsagePolicySettings secondSettings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 15),
            new TimeOnly(3, 0));

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(firstSettings, timeProvider), LockReminderSettings.Default));
        AssertReminderAt(coordinator, timeProvider, secondSettings, new DateTimeOffset(2026, 9, 22, 0, 10, 1, TimeSpan.Zero), null);
        AssertReminderAt(coordinator, timeProvider, secondSettings, new DateTimeOffset(2026, 9, 22, 0, 14, 0, TimeSpan.Zero), 1);
    }

    [TestMethod]
    public void EvaluateDoesNotReplayPastReminderWhenSettingsAreEnabledAfterThreshold()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 11, 49, 0, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(12, 0),
            new TimeOnly(13, 0));
        LockReminderSettings tenDisabled = LockReminderSettings.Default.WithEnabled(10, false);

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), tenDisabled));
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 11, 50, 0, TimeSpan.Zero), null, tenDisabled);
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 11, 51, 0, TimeSpan.Zero), null);
        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 11, 55, 0, TimeSpan.Zero), 5);
    }

    [TestMethod]
    public void EvaluateTreatsSmallEmergencyEndJitterAsTheSameTarget()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 11, 59, 59, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 0),
            new TimeOnly(13, 0));
        var firstTarget = new DateTime(2026, 9, 22, 12, 10, 0);
        var jitteredTarget = new DateTime(2026, 9, 22, 12, 10, 0, 500);

        Assert.IsNull(coordinator.Evaluate(
            CreateSnapshot(settings, timeProvider, firstTarget),
            LockReminderSettings.Default));
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 22, 12, 0, 1, TimeSpan.Zero));

        Assert.AreEqual(
            10,
            coordinator.Evaluate(
                CreateSnapshot(settings, timeProvider, jitteredTarget),
                LockReminderSettings.Default));
    }

    [TestMethod]
    public void EvaluateResetsBaselineWhenWallClockJumpsAheadOfMonotonicTime()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 11, 40, 0, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(12, 0),
            new TimeOnly(13, 0));

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));
        timeProvider.SetUtcNowWithoutElapsed(
            new DateTimeOffset(2026, 9, 22, 11, 50, 0, TimeSpan.Zero));
        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));

        AssertReminderAt(coordinator, timeProvider, settings, new DateTimeOffset(2026, 9, 22, 11, 55, 0, TimeSpan.Zero), 5);
    }

    [TestMethod]
    public void EvaluateAllowsSmallDelayWhenNewEmergencyTargetStartsAtALeadTime()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 0, 20, 0, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 0),
            new TimeOnly(3, 0));

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));
        timeProvider.SetUtcNow(
            new DateTimeOffset(2026, 9, 22, 0, 20, 0, TimeSpan.Zero).AddMilliseconds(250));

        Assert.AreEqual(
            10,
            coordinator.Evaluate(
                CreateSnapshot(settings, timeProvider, new DateTime(2026, 9, 22, 0, 30, 0)),
                LockReminderSettings.Default));
    }

    [TestMethod]
    public void EvaluateSkipsNewEmergencyTargetReminderWhenItsLeadTimeIsTooOld()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 0, 20, 0, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(0, 0),
            new TimeOnly(3, 0));

        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 22, 0, 20, 6, TimeSpan.Zero));

        Assert.IsNull(coordinator.Evaluate(
            CreateSnapshot(settings, timeProvider, new DateTime(2026, 9, 22, 0, 30, 0)),
            LockReminderSettings.Default));
        AssertReminderAt(
            coordinator,
            timeProvider,
            settings,
            new DateTimeOffset(2026, 9, 22, 0, 25, 0, TimeSpan.Zero),
            5,
            emergencyUnlockEndLocalTime: new DateTime(2026, 9, 22, 0, 30, 0));
    }

    [TestMethod]
    public void ChangingAnotherReminderDoesNotDropAnEnabledBoundary()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 11, 49, 59, TimeSpan.Zero));
        var coordinator = new LockReminderCoordinator(timeProvider);
        UsagePolicySettings settings = CreateSettings(
            DayOfWeek.Tuesday,
            new TimeOnly(12, 0),
            new TimeOnly(13, 0));
        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(settings, timeProvider), LockReminderSettings.Default));

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 22, 11, 50, 0, TimeSpan.Zero));

        Assert.AreEqual(10, coordinator.Evaluate(
            CreateSnapshot(settings, timeProvider),
            new LockReminderSettings(ThirtyMinutes: false)));
    }

    [TestMethod]
    public void DiagnosticsDoNotWriteOnEveryStableRefreshOrWhenTargetIsRemoved()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var events = new List<string>();
        var coordinator = new LockReminderCoordinator(clock, new LockReminderDiagnostics(events.Add));
        UsagePolicySettings policy = CreateSettings(DayOfWeek.Wednesday, new TimeOnly(19, 10), new TimeOnly(23, 0));
        coordinator.Evaluate(CreateSnapshot(policy, clock), LockReminderSettings.Default);
        int initialCount = events.Count;
        for (int second = 1; second <= 3600; second++)
        {
            clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero).AddSeconds(second));
            coordinator.Evaluate(CreateSnapshot(policy, clock), LockReminderSettings.Default);
        }
        Assert.HasCount(initialCount, events);

        UsagePolicySettings noSchedule = new(WeeklyUsageRestrictionSchedule.Default, EmergencyUnlockSettings.Default, []);
        coordinator.Evaluate(CreateSnapshot(noSchedule, clock), LockReminderSettings.Default);
        int removedCount = events.Count;
        clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 13, 0, 1, TimeSpan.Zero));
        coordinator.Evaluate(CreateSnapshot(noSchedule, clock), LockReminderSettings.Default);
        Assert.HasCount(removedCount, events);
        Assert.IsGreaterThan(initialCount, removedCount);
    }

    [TestMethod]
    [DataRow(true, 6, "observation-late")]
    [DataRow(false, 1, "disabled")]
    public void DiagnosticsIdentifyWhyACrossedFiveMinuteReminderWasSkipped(bool enabled, int delaySeconds, string reason)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 19, 4, 59, TimeSpan.Zero));
        var events = new List<string>();
        var coordinator = new LockReminderCoordinator(clock, new LockReminderDiagnostics(events.Add));
        UsagePolicySettings policy = CreateSettings(DayOfWeek.Wednesday, new TimeOnly(19, 10), new TimeOnly(23, 0));
        var reminders = new LockReminderSettings(FiveMinutes: enabled);
        coordinator.Evaluate(CreateSnapshot(policy, clock), reminders);
        clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 19, 5, delaySeconds, TimeSpan.Zero));
        Assert.IsNull(coordinator.Evaluate(CreateSnapshot(policy, clock), reminders));
        Assert.IsTrue(events.Any(entry => entry.Contains($"skipped reason={reason} minutes=5", StringComparison.Ordinal)));
    }

    private static void AssertReminderAt(
        LockReminderCoordinator coordinator,
        ManualTimeProvider timeProvider,
        UsagePolicySettings settings,
        DateTimeOffset localTime,
        int? expectedMinutes,
        LockReminderSettings? reminderSettings = null,
        DateTime? emergencyUnlockEndLocalTime = null)
    {
        timeProvider.SetUtcNow(localTime);

        Assert.AreEqual(
            expectedMinutes,
            coordinator.Evaluate(
                CreateSnapshot(settings, timeProvider, emergencyUnlockEndLocalTime),
                reminderSettings ?? LockReminderSettings.Default));
    }

    private static UsagePolicyRuntimeSnapshot CreateSnapshot(
        UsagePolicySettings settings,
        ManualTimeProvider timeProvider,
        DateTime? emergencyUnlockEndLocalTime = null)
    {
        DateTime localNow = timeProvider.GetLocalNow().DateTime;
        bool emergencyUnlockActive =
            emergencyUnlockEndLocalTime is DateTime unlockEnd && localNow < unlockEnd;
        UsagePolicyEvaluation evaluation = UsagePolicyEvaluator.Evaluate(
            settings,
            localNow,
            emergencyUnlockActive);

        return new UsagePolicyRuntimeSnapshot(
            settings,
            evaluation,
            localNow,
            HasPendingEmergencyChallenge: false,
            EmergencyUnlockEndLocalTime: emergencyUnlockActive ? emergencyUnlockEndLocalTime : null,
            NextLockStartLocalTime: UsagePolicyEvaluator.FindNextLockStart(
                settings,
                localNow,
                emergencyUnlockEndLocalTime));
    }

    private static UsagePolicySettings CreateSettings(
        DayOfWeek day,
        TimeOnly start,
        TimeOnly release,
        IReadOnlyList<OutOfHoursReservation>? reservations = null) =>
        new(
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(
                day,
                new DailyUsageRestriction(true, start, release)),
            EmergencyUnlockSettings.Default,
            reservations ?? Array.Empty<OutOfHoursReservation>());

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void SetUtcNow(DateTimeOffset utcNow)
        {
            if (utcNow > _utcNow)
            {
                _timestamp = checked(_timestamp + (utcNow - _utcNow).Ticks);
            }

            _utcNow = utcNow;
        }

        public void SetUtcNowWithoutElapsed(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
