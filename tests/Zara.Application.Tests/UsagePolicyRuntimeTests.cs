using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;

namespace Zara.Application.Tests;

[TestClass]
public sealed class UsagePolicyRuntimeTests
{
    private static readonly bool[] ExpectedInitialLockRequirement = [true];
    private static readonly bool[] ExpectedLockThenUnlockRequirements = [true, false];
    private static readonly bool[] ExpectedUnlockThenLockRequirements = [true, false, true];
    private static readonly bool[] ExpectedEmergencyBoundaryOverlays = [true, false, false, false];
    private static readonly bool[] ExpectedEmergencyBoundaryRecovery = [true, true, false, true];
    private static readonly bool[] ExpectedEmergencyBoundaryRelock = [true, false, false, false, true];
#if DEBUG
    private static readonly bool[] ExpectedEmergencyThenDevelopmentRequirements = [true, false, false];
#endif

    [TestMethod]
    public async Task InitializeAppliesPersistedLockRequirement()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0)));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);

        await runtime.InitializeAsync();

        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        CollectionAssert.AreEqual(ExpectedInitialLockRequirement, lockPort.AppliedRequirements);
    }

    [TestMethod]
    public async Task InitializeRemovesEndedReservationsAndPersistsThemAcrossRestarts()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 9, 22, 15, 0, 0, TimeSpan.Zero));
        var ended = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 22), new TimeOnly(0, 0), new TimeOnly(1, 0), "종료");
        var active = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 22), new TimeOnly(14, 0), new TimeOnly(16, 0), "적용 중");
        var future = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 23), new TimeOnly(0, 0), new TimeOnly(1, 0), "미래");
        var settings = CreateSettings(
            DayOfWeek.Tuesday, new TimeOnly(0, 0), new TimeOnly(23, 0),
            reservations: [ended, active, future]);
        var store = new RecordingStore(settings);
        using (var runtime = CreateRuntime(store, new RecordingLockPort(), new RecordingPromptCatalog(), timeProvider))
        {
            await runtime.InitializeAsync();

            CollectionAssert.AreEqual(new[] { active, future }, store.Settings.Reservations.ToArray());
            Assert.AreSame(settings.WeeklySchedule, store.Settings.WeeklySchedule);
            Assert.AreSame(settings.EmergencyUnlock, store.Settings.EmergencyUnlock);
            Assert.AreSame(store.Settings, runtime.CurrentSnapshot.Settings);
            Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.HasActiveReservation);
            Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.LockRequired);
        }

        using var restarted = CreateRuntime(store, new RecordingLockPort(), new RecordingPromptCatalog(), timeProvider);
        await restarted.InitializeAsync();
        await restarted.RefreshAsync();

        CollectionAssert.AreEqual(new[] { active, future }, restarted.CurrentSnapshot.Settings.Reservations.ToArray());
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshRemovesReservationAtItsEndWithoutChangingEmergencyUnlock(bool emergencyUnlockActive)
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 9, 22, 0, 59, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 22), new TimeOnly(0, 0), new TimeOnly(1, 0), "종료 대상");
        var settings = CreateSettings(
            DayOfWeek.Tuesday, new TimeOnly(0, 0), new TimeOnly(7, 0),
            sentenceCount: 0, reservations: [reservation]);
        var store = new RecordingStore(settings);
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        if (emergencyUnlockActive)
        {
            await runtime.StartEmergencyUnlockAsync();
        }

        await runtime.RefreshAsync();
        Assert.AreEqual(0, store.SaveCount);
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero));
        timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        await runtime.RefreshAsync();

        Assert.IsEmpty(store.Settings.Reservations);
        Assert.IsEmpty(runtime.CurrentSnapshot.Settings.Reservations);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.HasActiveReservation);
        Assert.AreEqual(emergencyUnlockActive, runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.AreEqual(!emergencyUnlockActive, lockPort.AppliedRequirements.Last());
        Assert.IsTrue(lockPort.RestartLockRequirements.Last());
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.IsSettingsChangeAllowed);
        if (emergencyUnlockActive)
        {
            Assert.AreEqual(new DateTime(2026, 9, 22, 1, 9, 0), runtime.CurrentSnapshot.EmergencyUnlockEndLocalTime);
        }

        // Moving the clock backward cannot restore a reservation that was already deleted.
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 22, 0, 30, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();
        await runtime.RefreshAsync();
        Assert.IsEmpty(runtime.CurrentSnapshot.Settings.Reservations);
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedCleanupAtStartupStillAppliesTheLockAndRetries(bool accessDenied)
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 22), new TimeOnly(0, 0), new TimeOnly(1, 0), "종료 대상");
        var settings = CreateSettings(
            DayOfWeek.Tuesday, new TimeOnly(0, 0), new TimeOnly(7, 0), reservations: [reservation]);
        var store = new RecordingStore(settings)
        {
            SaveException = accessDenied ? new UnauthorizedAccessException("읽기 전용") : new IOException("저장 실패"),
        };
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);

        await runtime.InitializeAsync();

        Assert.IsTrue(lockPort.AppliedRequirements.Last());
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.HasActiveReservation);
        Assert.AreSame(settings, runtime.CurrentSnapshot.Settings);
        Assert.AreSame(settings, store.Settings);

        store.SaveException = null;
        await runtime.RefreshAsync();
        Assert.IsEmpty(store.Settings.Reservations);
        Assert.IsEmpty(runtime.CurrentSnapshot.Settings.Reservations);
        Assert.AreEqual(2, store.SaveCount);
        Assert.HasCount(1, lockPort.AppliedRequirements);
    }

    [TestMethod]
    public async Task FailedCleanupSaveDoesNotPreventRelockingAtTheReservationEnd()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 9, 22, 0, 30, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 22), new TimeOnly(0, 0), new TimeOnly(1, 0), "종료 대상");
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Tuesday, new TimeOnly(0, 0), new TimeOnly(7, 0), reservations: [reservation]));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        store.SaveException = new IOException("저장 실패");
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero));

        await runtime.RefreshAsync();

        Assert.IsTrue(lockPort.AppliedRequirements.Last());
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.HasActiveReservation);
        Assert.HasCount(1, store.Settings.Reservations);
        store.SaveException = null;
        await runtime.RefreshAsync();
        Assert.IsEmpty(store.Settings.Reservations);
        Assert.IsEmpty(runtime.CurrentSnapshot.Settings.Reservations);
    }

    [TestMethod]
    public async Task SettingsSaveAlsoRemovesExpiredReservationsWithoutAnExtraWrite()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 9, 22, 0, 30, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 22), new TimeOnly(0, 0), new TimeOnly(1, 0), "종료 대상");
        var store = new RecordingStore(UsagePolicySettings.Default.WithReservations([reservation]));
        using var runtime = CreateRuntime(store, new RecordingLockPort(), new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 22, 15, 0, 0, TimeSpan.Zero));
        var emergencySettings = new EmergencyUnlockSettings(20, 0);

        await runtime.UpdateEmergencyUnlockSettingsAsync(emergencySettings);

        Assert.IsEmpty(store.Settings.Reservations);
        Assert.IsEmpty(runtime.CurrentSnapshot.Settings.Reservations);
        Assert.AreEqual(emergencySettings, store.Settings.EmergencyUnlock);
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    public async Task RefreshUsesChangedWallClockForRestrictionButNotEmergencyElapsedTime()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero));
        var settings = CreateSettings(
            DayOfWeek.Monday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0),
            sentenceCount: 0);
        var store = new RecordingStore(settings);
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();

        EmergencyUnlockStartResult result = await runtime.StartEmergencyUnlockAsync();
        Assert.IsTrue(result.StartedImmediately);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.LockRequired);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 23, 0, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();

        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.LockRequired);

        timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(10));
        await runtime.RefreshAsync();

        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        CollectionAssert.AreEqual(
            ExpectedUnlockThenLockRequirements,
            lockPort.AppliedRequirements);
    }

    [TestMethod]
    public async Task RefreshDoesNotExtendEmergencyUnlockWhenWallClockMovesBackward()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0),
            sentenceCount: 0));
        using var runtime = CreateRuntime(store, new RecordingLockPort(), new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        await runtime.StartEmergencyUnlockAsync();

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 21, 0, 0, TimeSpan.Zero));
        timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(10));
        await runtime.RefreshAsync();

        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
    }

    [TestMethod]
    public async Task SavingASecondRestrictionAfterTheFirstEndsLocksAtTheNewStartTime()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday,
            new TimeOnly(9, 0),
            new TimeOnly(9, 1)));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 9, 1, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();

        WeeklyUsageRestrictionSchedule secondSchedule = CreateSchedule(
            DayOfWeek.Monday,
            new TimeOnly(9, 2),
            new TimeOnly(10, 0));
        await runtime.UpdateSettingsAsync(secondSchedule, EmergencyUnlockSettings.Default);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 9, 2, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();

        UsagePolicySettings persisted = await store.LoadAsync();
        Assert.AreEqual(secondSchedule, persisted.WeeklySchedule);
        Assert.AreEqual(1, store.SaveCount);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        CollectionAssert.AreEqual(
            ExpectedUnlockThenLockRequirements,
            lockPort.AppliedRequirements);
    }

    [TestMethod]
    public async Task EmergencyUnlockSuppressesASecondRestrictionUntilItsDurationExpires()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday,
            new TimeOnly(9, 0),
            new TimeOnly(9, 1),
            sentenceCount: 0));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        await runtime.StartEmergencyUnlockAsync();

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 9, 1, 0, TimeSpan.Zero));
        timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        await runtime.RefreshAsync();

        WeeklyUsageRestrictionSchedule secondSchedule = CreateSchedule(
            DayOfWeek.Monday,
            new TimeOnly(9, 2),
            new TimeOnly(10, 0));
        await runtime.UpdateSettingsAsync(
            secondSchedule,
            new EmergencyUnlockSettings(durationMinutes: 10, sentenceCount: 0));

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 9, 2, 0, TimeSpan.Zero));
        timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        await runtime.RefreshAsync();

        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.IsWithinUsageBan);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.LockRequired);
        CollectionAssert.AreEqual(
            ExpectedEmergencyBoundaryOverlays,
            lockPort.AppliedRequirements);
        CollectionAssert.AreEqual(ExpectedEmergencyBoundaryRecovery, lockPort.RestartLockRequirements);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 9, 10, 0, TimeSpan.Zero));
        timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(8));
        await runtime.RefreshAsync();

        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        CollectionAssert.AreEqual(
            ExpectedEmergencyBoundaryRelock,
            lockPort.AppliedRequirements);
    }

    [TestMethod]
    public async Task StartEmergencyUnlockRefreshesTheCurrentWallClockBeforeGrantingAccess()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0),
            sentenceCount: 0));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => runtime.StartEmergencyUnlockAsync());

        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.IsWithinUsageBan);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        CollectionAssert.AreEqual(ExpectedLockThenUnlockRequirements, lockPort.AppliedRequirements);
    }

    [TestMethod]
    public async Task ClearEmergencyUnlockImmediatelyReappliesBaseRestriction()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0),
            sentenceCount: 0));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        await runtime.StartEmergencyUnlockAsync();

        await runtime.ClearEmergencyUnlockAsync();

        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        CollectionAssert.AreEqual(
            ExpectedUnlockThenLockRequirements,
            lockPort.AppliedRequirements);
    }

    [TestMethod]
    public async Task PromptChallengeRequiresExactTextBeforeStartingUnlock()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero));
        var prompts = new RecordingPromptCatalog(["첫 번째 문장입니다.", "두 번째 문장입니다."]);
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0),
            sentenceCount: 2));
        using var runtime = CreateRuntime(store, new RecordingLockPort(), prompts, timeProvider);
        await runtime.InitializeAsync();

        EmergencyUnlockStartResult start = await runtime.StartEmergencyUnlockAsync();
        Assert.IsFalse(start.StartedImmediately);
        Assert.IsTrue(runtime.CurrentSnapshot.HasPendingEmergencyChallenge);
        Assert.AreEqual("첫 번째 문장입니다.\n두 번째 문장입니다.", start.Challenge!.ExpectedText);

        Assert.IsFalse(await runtime.CompleteEmergencyUnlockAsync("첫 번째 문장입니다."));
        Assert.IsTrue(runtime.CurrentSnapshot.HasPendingEmergencyChallenge);
        Assert.IsTrue(await runtime.CompleteEmergencyUnlockAsync(
            "첫 번째 문장입니다.\r\n두 번째 문장입니다."));
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.IsFalse(runtime.CurrentSnapshot.HasPendingEmergencyChallenge);
    }

    [TestMethod]
    public async Task SettingChangesAreRejectedDuringBaseRestrictionEvenWithActiveReservation()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 30, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 8, 10),
            new TimeOnly(22, 0),
            new TimeOnly(23, 0),
            "독서 모임");
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0),
            reservations: [reservation]));
        using var runtime = CreateRuntime(store, new RecordingLockPort(), new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();

        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.HasActiveReservation);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.IsSettingsChangeAllowed);
        await Assert.ThrowsExactlyAsync<UsagePolicySettingsLockedException>(
            () => runtime.UpdateSettingsAsync(
                WeeklyUsageRestrictionSchedule.Default,
                EmergencyUnlockSettings.Default));
        await Assert.ThrowsExactlyAsync<UsagePolicySettingsLockedException>(
            () => runtime.UpdateWeeklyScheduleAsync(WeeklyUsageRestrictionSchedule.Default));
        await Assert.ThrowsExactlyAsync<UsagePolicySettingsLockedException>(
            () => runtime.UpdateEmergencyUnlockSettingsAsync(new EmergencyUnlockSettings(15, 0)));
        await Assert.ThrowsExactlyAsync<UsagePolicySettingsLockedException>(
            () => runtime.AddReservationAsync(new OutOfHoursReservation(
                Guid.NewGuid(), new DateOnly(2026, 8, 11), new TimeOnly(10, 0), new TimeOnly(11, 0), "추가")));
        Assert.AreEqual(0, store.SaveCount);
    }

    [TestMethod]
    [DataRow(12, false, false)]
    [DataRow(22, false, true)]
    [DataRow(22, true, false)]
    public async Task RemovingActiveReservationPersistsAndImmediatelyReevaluatesLock(
        int hour,
        bool emergencyUnlockActive,
        bool expectedLock)
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, hour, 30, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 10), new TimeOnly(hour, 0), new TimeOnly(hour + 1, 0), "종료");
        var future = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 11), new TimeOnly(10, 0), new TimeOnly(11, 0), "유지");
        var settings = CreateSettings(
            DayOfWeek.Monday, new TimeOnly(21, 0), new TimeOnly(7, 0),
            sentenceCount: 0, reservations: [reservation, future]);
        var store = new RecordingStore(settings);
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        if (emergencyUnlockActive)
        {
            await runtime.StartEmergencyUnlockAsync();
            timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(4));
            timeProvider.SetUtcNow(timeProvider.GetUtcNow().AddMinutes(4));
            await runtime.RefreshAsync();
        }

        DateTime? emergencyEnd = runtime.CurrentSnapshot.EmergencyUnlockEndLocalTime;
        ReservationChangeStatus result = await runtime.RemoveReservationAsync(reservation.Id);

        Assert.AreEqual(ReservationChangeStatus.Removed, result);
        Assert.AreEqual(1, store.SaveCount);
        CollectionAssert.AreEqual(new[] { future }, store.Settings.Reservations.ToArray());
        Assert.AreEqual(store.Settings, runtime.CurrentSnapshot.Settings);
        Assert.AreEqual(settings.WeeklySchedule, store.Settings.WeeklySchedule);
        Assert.AreEqual(settings.EmergencyUnlock, store.Settings.EmergencyUnlock);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.HasActiveReservation);
        Assert.AreEqual(expectedLock, runtime.CurrentSnapshot.Evaluation.LockRequired);
        Assert.AreEqual(expectedLock, lockPort.AppliedRequirements.Last());
        Assert.AreEqual(emergencyUnlockActive, runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.AreEqual(emergencyEnd, runtime.CurrentSnapshot.EmergencyUnlockEndLocalTime);
        Assert.AreEqual(hour == 12, runtime.CurrentSnapshot.Evaluation.IsSettingsChangeAllowed);
        if (emergencyUnlockActive)
        {
            timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(6));
            await runtime.RefreshAsync();
            Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
            Assert.IsTrue(lockPort.AppliedRequirements.Last());
        }
    }

    [TestMethod]
    [DataRow(21, 59, 22, 0)]
    [DataRow(22, 59, 23, 0)]
    public async Task RemovingReservationReevaluatesCurrentTimeBeforeTheNextRefresh(
        int initialHour,
        int initialMinute,
        int removalHour,
        int removalMinute)
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, initialHour, initialMinute, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 10), new TimeOnly(22, 0), new TimeOnly(23, 0), "경계");
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday, new TimeOnly(21, 0), new TimeOnly(7, 0), reservations: [reservation]));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, removalHour, removalMinute, 0, TimeSpan.Zero));

        Assert.AreEqual(ReservationChangeStatus.Removed, await runtime.RemoveReservationAsync(reservation.Id));
        Assert.IsEmpty(store.Settings.Reservations);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        Assert.IsTrue(lockPort.AppliedRequirements.Last());
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RemovingFutureReservationsAfterExpiredCleanupPreservesCurrentLockConditions(
        bool activeReservation,
        bool emergencyUnlockActive)
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 30, 0, TimeSpan.Zero));
        var active = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 10), new TimeOnly(22, 0), new TimeOnly(23, 0), "적용 중");
        var future = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 11), new TimeOnly(10, 0), new TimeOnly(11, 0), "미래");
        var expired = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 9), new TimeOnly(10, 0), new TimeOnly(11, 0), "지난 예약");
        var settings = CreateSettings(
            DayOfWeek.Monday, new TimeOnly(21, 0), new TimeOnly(7, 0),
            sentenceCount: 0, reservations: activeReservation ? [active, future, expired] : [future, expired]);
        var store = new RecordingStore(settings);
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        if (emergencyUnlockActive)
        {
            await runtime.StartEmergencyUnlockAsync();
        }

        int lockApplyCount = lockPort.AppliedRequirements.Count;
        Assert.AreEqual(ReservationChangeStatus.Removed, await runtime.RemoveReservationAsync(future.Id));
        Assert.AreEqual(ReservationChangeStatus.NotFound, await runtime.RemoveReservationAsync(expired.Id));

        Assert.AreEqual(2, store.SaveCount);
        Assert.HasCount(activeReservation ? 1 : 0, store.Settings.Reservations);
        Assert.AreEqual(store.Settings, runtime.CurrentSnapshot.Settings);
        Assert.AreEqual(activeReservation, runtime.CurrentSnapshot.Evaluation.HasActiveReservation);
        Assert.AreEqual(emergencyUnlockActive, runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.AreEqual(!activeReservation && !emergencyUnlockActive, runtime.CurrentSnapshot.Evaluation.LockRequired);
        Assert.HasCount(lockApplyCount, lockPort.AppliedRequirements);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.IsSettingsChangeAllowed);
    }

    [TestMethod]
    public async Task RemovingFutureReservationUpdatesTheNextLockStart()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 10), new TimeOnly(21, 0), new TimeOnly(23, 0), "미래");
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday, new TimeOnly(21, 0), new TimeOnly(7, 0), reservations: [reservation]));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        Assert.AreEqual(new DateTime(2026, 8, 10, 23, 0, 0), runtime.CurrentSnapshot.NextLockStartLocalTime);

        Assert.AreEqual(ReservationChangeStatus.Removed, await runtime.RemoveReservationAsync(reservation.Id));

        Assert.AreEqual(new DateTime(2026, 8, 10, 21, 0, 0), runtime.CurrentSnapshot.NextLockStartLocalTime);
        Assert.IsFalse(lockPort.AppliedRequirements.Single());
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 21, 0, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();
        Assert.IsTrue(lockPort.AppliedRequirements.Last());
    }

    [TestMethod]
    public async Task FailedActiveReservationRemovalSaveKeepsTheReservationAndUnlock()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 30, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 10), new TimeOnly(22, 0), new TimeOnly(23, 0), "유지");
        var settings = CreateSettings(
            DayOfWeek.Monday, new TimeOnly(21, 0), new TimeOnly(7, 0), reservations: [reservation]);
        var store = new RecordingStore(settings) { SaveException = new IOException("Disk unavailable.") };
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();

        await Assert.ThrowsExactlyAsync<IOException>(() => runtime.RemoveReservationAsync(reservation.Id));

        Assert.AreEqual(settings, store.Settings);
        Assert.AreEqual(settings, runtime.CurrentSnapshot.Settings);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.HasActiveReservation);
        Assert.IsFalse(lockPort.AppliedRequirements.Single());
    }

    [TestMethod]
    public async Task FailedRelockAfterActiveReservationRemovalKeepsDeletionAndRetries()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 30, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 10), new TimeOnly(22, 0), new TimeOnly(23, 0), "종료");
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday, new TimeOnly(21, 0), new TimeOnly(7, 0), reservations: [reservation]));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        lockPort.FailWhenLocking = true;

        await Assert.ThrowsExactlyAsync<UsagePolicySettingsSavedButApplyFailedException>(
            () => runtime.RemoveReservationAsync(reservation.Id));

        Assert.AreEqual(1, store.SaveCount);
        Assert.IsEmpty(store.Settings.Reservations);
        Assert.AreEqual(store.Settings, runtime.CurrentSnapshot.Settings);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        lockPort.FailWhenLocking = false;
        await runtime.RefreshAsync();
        Assert.HasCount(3, lockPort.AppliedRequirements);
        Assert.IsTrue(lockPort.AppliedRequirements.Last());
    }

    [TestMethod]
    public async Task AddingReservationsIsRejectedDuringAnActiveEmergencyUnlock()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 30, 0, TimeSpan.Zero));
        var store = new RecordingStore(CreateSettings(
            DayOfWeek.Monday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0),
            sentenceCount: 0));
        using var runtime = CreateRuntime(store, new RecordingLockPort(), new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        await runtime.StartEmergencyUnlockAsync();

        await Assert.ThrowsExactlyAsync<UsagePolicySettingsLockedException>(
            () => runtime.AddReservationAsync(new OutOfHoursReservation(
                Guid.NewGuid(),
                new DateOnly(2026, 8, 10),
                new TimeOnly(23, 0),
                new TimeOnly(23, 30),
                "긴급 해제 중 추가")));

        Assert.AreEqual(0, store.SaveCount);
    }

    [TestMethod]
    public async Task AddReservationReturnsConflictWithoutPersisting()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var existing = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 10),
            new TimeOnly(0, 0),
            new TimeOnly(2, 0),
            "기존");
        var store = new RecordingStore(new UsagePolicySettings(
            WeeklyUsageRestrictionSchedule.Default,
            EmergencyUnlockSettings.Default,
            [existing]));
        using var runtime = CreateRuntime(store, new RecordingLockPort(), new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();

        ReservationChangeStatus status = await runtime.AddReservationAsync(new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 10),
            new TimeOnly(1, 30),
            new TimeOnly(3, 0),
            "겹침"));

        Assert.AreEqual(ReservationChangeStatus.ConflictsWithExisting, status);
        Assert.AreEqual(0, store.SaveCount);
    }

    [TestMethod]
    public async Task WeeklyScheduleUpdatePreservesEmergencySettingsAndReservations()
    {
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 10),
            new TimeOnly(13, 0),
            new TimeOnly(14, 0),
            "보존 대상");
        var emergency = new EmergencyUnlockSettings(durationMinutes: 23, sentenceCount: 5);
        var store = new RecordingStore(new UsagePolicySettings(
            WeeklyUsageRestrictionSchedule.Default,
            emergency,
            [reservation]));
        using var runtime = CreateRuntime(
            store,
            new RecordingLockPort(),
            new RecordingPromptCatalog(),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        WeeklyUsageRestrictionSchedule updatedSchedule = CreateSchedule(
            DayOfWeek.Tuesday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0));

        await runtime.UpdateWeeklyScheduleAsync(updatedSchedule);

        Assert.AreEqual(updatedSchedule, store.Settings.WeeklySchedule);
        Assert.AreEqual(emergency, store.Settings.EmergencyUnlock);
        CollectionAssert.AreEqual(
            new[] { reservation },
            store.Settings.Reservations.ToArray());
    }

    [TestMethod]
    public async Task EmergencySettingsUpdatePreservesWeeklyScheduleAndReservations()
    {
        WeeklyUsageRestrictionSchedule schedule = CreateSchedule(
            DayOfWeek.Tuesday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 10),
            new TimeOnly(13, 0),
            new TimeOnly(14, 0),
            "보존 대상");
        var store = new RecordingStore(new UsagePolicySettings(
            schedule,
            EmergencyUnlockSettings.Default,
            [reservation]));
        using var runtime = CreateRuntime(
            store,
            new RecordingLockPort(),
            new RecordingPromptCatalog(),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        var updatedEmergency = new EmergencyUnlockSettings(durationMinutes: 31, sentenceCount: 7);

        await runtime.UpdateEmergencyUnlockSettingsAsync(updatedEmergency);

        Assert.AreEqual(schedule, store.Settings.WeeklySchedule);
        Assert.AreEqual(updatedEmergency, store.Settings.EmergencyUnlock);
        CollectionAssert.AreEqual(
            new[] { reservation },
            store.Settings.Reservations.ToArray());
    }

#if DEBUG
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DevelopmentResetDisablesEveryWeekdayAndAllowsEditingDuringABan(
        bool emergencyUnlockActive)
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero));
        WeeklyUsageRestrictionSchedule schedule = WeeklyUsageRestrictionSchedule.Default;
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            schedule = schedule.WithRestriction(
                day,
                new DailyUsageRestriction(true, new TimeOnly(21, (int)day), new TimeOnly(7, (int)day)));
        }

        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 10), new TimeOnly(13, 0), new TimeOnly(14, 0), "보존 대상");
        var settings = new UsagePolicySettings(
            schedule, new EmergencyUnlockSettings(durationMinutes: 17, sentenceCount: 0), [reservation]);
        var store = new RecordingStore(settings);
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();
        if (emergencyUnlockActive)
        {
            await runtime.StartEmergencyUnlockAsync();
        }

        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.IsSettingsChangeAllowed);

        await runtime.DisableWeeklyScheduleForDevelopmentAsync();

        Assert.AreEqual(1, store.SaveCount);
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            DailyUsageRestriction persisted = store.Settings.WeeklySchedule.GetRestriction(day);
            Assert.IsFalse(persisted.IsEnabled);
            Assert.AreEqual(schedule.GetRestriction(day).StartTime, persisted.StartTime);
            Assert.AreEqual(schedule.GetRestriction(day).ReleaseTime, persisted.ReleaseTime);
        }

        Assert.AreEqual(settings.EmergencyUnlock, store.Settings.EmergencyUnlock);
        CollectionAssert.AreEqual(settings.Reservations, store.Settings.Reservations);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.IsSettingsChangeAllowed);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.LockRequired);
        Assert.AreEqual(emergencyUnlockActive, runtime.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        CollectionAssert.AreEqual(
            emergencyUnlockActive ? ExpectedEmergencyThenDevelopmentRequirements : ExpectedLockThenUnlockRequirements,
            lockPort.AppliedRequirements);
        Assert.IsFalse(lockPort.RestartLockRequirements.Last());

        await runtime.UpdateEmergencyUnlockSettingsAsync(new EmergencyUnlockSettings(18, 0));
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 11, 22, 0, 0, TimeSpan.Zero));
        timeProvider.AdvanceMonotonic(TimeSpan.FromDays(1));
        await runtime.RefreshAsync();
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.LockRequired);

        var restartedLockPort = new RecordingLockPort();
        using var restarted = CreateRuntime(store, restartedLockPort, new RecordingPromptCatalog(), timeProvider);
        await restarted.InitializeAsync();
        Assert.IsTrue(restarted.CurrentSnapshot.Evaluation.IsSettingsChangeAllowed);
        Assert.IsFalse(restartedLockPort.AppliedRequirements.Single());
    }

    [TestMethod]
    public async Task FailedDevelopmentResetSaveKeepsThePersistedScheduleAndReportsFailure()
    {
        var settings = CreateSettings(DayOfWeek.Monday, new TimeOnly(21, 0), new TimeOnly(7, 0));
        var store = new RecordingStore(settings) { SaveException = new IOException("Disk unavailable.") };
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(
            store,
            lockPort,
            new RecordingPromptCatalog(),
            new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();

        await Assert.ThrowsExactlyAsync<IOException>(
            () => runtime.DisableWeeklyScheduleForDevelopmentAsync());

        Assert.AreEqual(settings, store.Settings);
        Assert.AreEqual(settings, runtime.CurrentSnapshot.Settings);
        CollectionAssert.AreEqual(ExpectedInitialLockRequirement, lockPort.AppliedRequirements);
    }

    [TestMethod]
    public async Task FailedUnlockAfterDevelopmentResetRetainsDisabledScheduleAndRetries()
    {
        var store = new RecordingStore(
            CreateSettings(DayOfWeek.Monday, new TimeOnly(21, 0), new TimeOnly(7, 0)));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(
            store,
            lockPort,
            new RecordingPromptCatalog(),
            new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        lockPort.FailWhenUnlocking = true;

        await Assert.ThrowsExactlyAsync<UsagePolicySettingsSavedButApplyFailedException>(
            () => runtime.DisableWeeklyScheduleForDevelopmentAsync());

        Assert.IsFalse(store.Settings.WeeklySchedule.Monday.IsEnabled);
        Assert.AreEqual(store.Settings, runtime.CurrentSnapshot.Settings);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.IsSettingsChangeAllowed);
        lockPort.FailWhenUnlocking = false;
        await runtime.RefreshAsync();
        Assert.IsFalse(lockPort.AppliedRequirements.Last());
    }

#endif

    [TestMethod]
    public async Task ConcurrentTabSavesPreserveBothLatestChanges()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        using var runtime = CreateRuntime(
            store,
            new RecordingLockPort(),
            new RecordingPromptCatalog(),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        WeeklyUsageRestrictionSchedule updatedSchedule = CreateSchedule(
            DayOfWeek.Tuesday,
            new TimeOnly(21, 0),
            new TimeOnly(7, 0));
        var updatedEmergency = new EmergencyUnlockSettings(durationMinutes: 29, sentenceCount: 9);

        await Task.WhenAll(
            runtime.UpdateWeeklyScheduleAsync(updatedSchedule),
            runtime.UpdateEmergencyUnlockSettingsAsync(updatedEmergency));

        Assert.AreEqual(updatedSchedule, store.Settings.WeeklySchedule);
        Assert.AreEqual(updatedEmergency, store.Settings.EmergencyUnlock);
        Assert.AreEqual(2, store.SaveCount);
    }

    [TestMethod]
    public async Task FailedSettingsSaveLeavesCurrentSnapshotUnchanged()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(UsagePolicySettings.Default)
        {
            SaveException = new IOException("Disk unavailable."),
        };
        using var runtime = CreateRuntime(store, new RecordingLockPort(), new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();

        await Assert.ThrowsExactlyAsync<IOException>(
            () => runtime.UpdateSettingsAsync(
                CreateSchedule(DayOfWeek.Monday, new TimeOnly(21, 0), new TimeOnly(7, 0)),
                EmergencyUnlockSettings.Default));

        Assert.AreEqual(WeeklyUsageRestrictionSchedule.Default, runtime.CurrentSnapshot.Settings.WeeklySchedule);
    }

    [TestMethod]
    public async Task FailedLockApplicationAfterSaveKeepsPersistedSettingsForTheNextRefresh()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(UsagePolicySettings.Default);
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort, new RecordingPromptCatalog(), timeProvider);
        await runtime.InitializeAsync();

        WeeklyUsageRestrictionSchedule updatedSchedule = CreateSchedule(
            DayOfWeek.Monday,
            new TimeOnly(9, 0),
            new TimeOnly(18, 0));
        lockPort.FailWhenLocking = true;

        UsagePolicySettingsSavedButApplyFailedException exception =
            await Assert.ThrowsExactlyAsync<UsagePolicySettingsSavedButApplyFailedException>(
            () => runtime.UpdateSettingsAsync(updatedSchedule, EmergencyUnlockSettings.Default));

        Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(updatedSchedule, runtime.CurrentSnapshot.Settings.WeeklySchedule);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);

        lockPort.FailWhenLocking = false;
        await runtime.RefreshAsync();

        Assert.IsTrue(lockPort.AppliedRequirements.Last());
    }

    private static UsagePolicyRuntime CreateRuntime(
        RecordingStore store,
        RecordingLockPort lockPort,
        RecordingPromptCatalog prompts,
        TimeProvider timeProvider) =>
        new(store, lockPort, prompts, timeProvider);

    private static UsagePolicySettings CreateSettings(
        DayOfWeek day,
        TimeOnly start,
        TimeOnly release,
        int sentenceCount = 3,
        IReadOnlyList<OutOfHoursReservation>? reservations = null) =>
        new(
            CreateSchedule(day, start, release),
            new EmergencyUnlockSettings(10, sentenceCount),
            reservations ?? Array.Empty<OutOfHoursReservation>());

    private static WeeklyUsageRestrictionSchedule CreateSchedule(
        DayOfWeek day,
        TimeOnly start,
        TimeOnly release) =>
        WeeklyUsageRestrictionSchedule.Default.WithRestriction(
            day,
            new DailyUsageRestriction(
                isEnabled: true,
                startTime: start,
                releaseTime: release));

    private sealed class RecordingStore : IUsagePolicySettingsStore
    {
        private UsagePolicySettings _settings;

        public RecordingStore(UsagePolicySettings settings)
        {
            _settings = settings;
        }

        public Exception? SaveException { get; set; }

        public int SaveCount { get; private set; }

        public UsagePolicySettings Settings => _settings;

        public Task<UsagePolicySettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task SaveAsync(
            UsagePolicySettings settings,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (SaveException is not null)
            {
                return Task.FromException(SaveException);
            }

            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLockPort : IUsagePolicyLockPort
    {
        public List<bool> AppliedRequirements { get; } = [];

        public List<bool> RestartLockRequirements { get; } = [];

        public bool FailWhenLocking { get; set; }

        public bool FailWhenUnlocking { get; set; }

        public Task ApplyPolicyLockRequirementAsync(
            bool lockRequired,
            bool lockRequiredAfterRestart,
            CancellationToken cancellationToken = default)
        {
            AppliedRequirements.Add(lockRequired);
            RestartLockRequirements.Add(lockRequiredAfterRestart);
            if ((lockRequired && FailWhenLocking) || (!lockRequired && FailWhenUnlocking))
            {
                return Task.FromException(new InvalidOperationException("The lock could not be applied."));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPromptCatalog : IEmergencyUnlockPromptCatalog
    {
        private readonly IReadOnlyList<string> _sentences;

        public RecordingPromptCatalog(IReadOnlyList<string>? sentences = null)
        {
            _sentences = sentences ?? ["기본 문장입니다.", "다른 문장입니다.", "마지막 문장입니다."];
        }

        public Task<IReadOnlyList<string>> SelectDistinctAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_sentences.Take(count).ToArray());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        private long _timestamp;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;

        public void AdvanceMonotonic(TimeSpan elapsed) =>
            _timestamp = checked(_timestamp + elapsed.Ticks);
    }
}
