using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;

namespace Zara.Application.Tests;

[TestClass]
public sealed class UsagePolicyRuntimeTests
{
    private static readonly bool[] ExpectedInitialLockRequirement = [true];
    private static readonly bool[] ExpectedLockThenUnlockRequirements = [true, false];
    private static readonly bool[] ExpectedUnlockThenLockRequirements = [true, false, true];

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
            () => runtime.RemoveReservationAsync(reservation.Id));
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

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => runtime.UpdateSettingsAsync(updatedSchedule, EmergencyUnlockSettings.Default));

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

        public Exception? SaveException { get; init; }

        public int SaveCount { get; private set; }

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

        public bool FailWhenLocking { get; set; }

        public Task ApplyPolicyLockRequirementAsync(
            bool lockRequired,
            CancellationToken cancellationToken = default)
        {
            AppliedRequirements.Add(lockRequired);
            if (lockRequired && FailWhenLocking)
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
