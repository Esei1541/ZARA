using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class BulkWeeklyScheduleTests
{
    [STATestMethod]
    public async Task ApplyingSubsetPreservesOtherDraftsAndDoesNotSaveOrChangeLockState()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort: lockPort);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        DailyUsageRestrictionViewModel friday = Day(viewModel, DayOfWeek.Friday);
        friday.IsRestrictionEnabled = true;
        friday.StartTime.Set(new TimeOnly(18, 15));
        friday.ReleaseTime.Set(new TimeOnly(20, 45));
        UsagePolicySettings saved = store.Settings;
        int lockCalls = lockPort.AppliedRequirements.Count;
        MainWindowNotificationEventArgs? notification = null;
        viewModel.NotificationRequested += (_, value) => notification = value;
        ConfigureBulk(viewModel, new TimeOnly(22, 15), new TimeOnly(5, 45), DayOfWeek.Monday, DayOfWeek.Sunday);

        viewModel.ApplyBulkScheduleCommand.Execute(null);

        foreach (DayOfWeek day in new[] { DayOfWeek.Monday, DayOfWeek.Sunday })
        {
            AssertDraft(Day(viewModel, day), true, new TimeOnly(22, 15), new TimeOnly(5, 45));
            Assert.IsTrue(Day(viewModel, day).HasChanges);
        }
        AssertDraft(friday, true, new TimeOnly(18, 15), new TimeOnly(20, 45));
        Assert.IsFalse(Day(viewModel, DayOfWeek.Tuesday).HasChanges);
        Assert.AreSame(saved, store.Settings);
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreSame(saved, runtime.CurrentSnapshot.Settings);
        Assert.HasCount(lockCalls, lockPort.AppliedRequirements);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.LockRequired);
        Assert.IsNotNull(notification);
        Assert.AreEqual("한 번에 적용", notification.Title);
        Assert.AreEqual("선택한 요일에 반영되었습니다.\n일정 저장을 누르면 잠금이 저장됩니다.", notification.Message);
        Assert.IsFalse(notification.IsError);
    }

    [STATestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ActivationOptionUsesEachSelectedDaysCurrentDraftState(bool activate)
    {
        var schedule = WeeklyUsageRestrictionSchedule.Default.WithRestriction(DayOfWeek.Tuesday,
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(10, 0)));
        using var runtime = CreateRuntime(new RecordingStore(UsagePolicySettings.Default.WithWeeklySchedule(schedule)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        Day(viewModel, DayOfWeek.Monday).IsRestrictionEnabled = true;
        Day(viewModel, DayOfWeek.Tuesday).IsRestrictionEnabled = false;
        ConfigureBulk(viewModel, new TimeOnly(23, 0), new TimeOnly(5, 0), DayOfWeek.Monday, DayOfWeek.Tuesday);
        viewModel.BulkScheduleEditor.ActivateAfterApply = activate;

        viewModel.ApplyBulkScheduleCommand.Execute(null);

        AssertDraft(Day(viewModel, DayOfWeek.Monday), true, new TimeOnly(23, 0), new TimeOnly(5, 0));
        AssertDraft(Day(viewModel, DayOfWeek.Tuesday), activate, new TimeOnly(23, 0), new TimeOnly(5, 0));
        Assert.IsTrue(viewModel.CanEditScheduleTime);
    }

    [STATestMethod]
    public async Task IndividualEditsAfterApplyingAreIncludedInOneScheduleSave()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        using var runtime = CreateRuntime(store);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        ConfigureBulk(viewModel, new TimeOnly(22, 0), new TimeOnly(23, 0), DayOfWeek.Monday, DayOfWeek.Thursday);
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        viewModel.SelectedWeekday = Day(viewModel, DayOfWeek.Monday);
        viewModel.SelectedWeekday.ReleaseTime.Set(new TimeOnly(23, 30));
        DailyUsageRestrictionViewModel friday = Day(viewModel, DayOfWeek.Friday);
        friday.IsRestrictionEnabled = true;
        friday.StartTime.Set(new TimeOnly(18, 15));
        friday.ReleaseTime.Set(new TimeOnly(20, 45));

        await viewModel.SaveWeeklyScheduleAsync();

        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(new DailyUsageRestriction(true, new TimeOnly(22, 0), new TimeOnly(23, 30)), store.Settings.WeeklySchedule.Monday);
        Assert.AreEqual(new DailyUsageRestriction(true, new TimeOnly(22, 0), new TimeOnly(23, 0)), store.Settings.WeeklySchedule.Thursday);
        Assert.AreEqual(friday.ToRestriction(), store.Settings.WeeklySchedule.Friday);
        Assert.IsFalse(viewModel.HasWeeklyScheduleChanges);
    }

    [STATestMethod]
    [DataRow("24", "00", "05", "00")]
    [DataRow("23", "60", "05", "00")]
    [DataRow("", "00", "05", "00")]
    [DataRow("23", "00", "24", "00")]
    [DataRow("23", "00", "23", "00")]
    public async Task InvalidGroupTimeCannotPartiallyChangeAnyTarget(string startHour, string startMinute, string releaseHour, string releaseMinute)
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        using var runtime = CreateRuntime(store);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        ConfigureBulk(viewModel, new TimeOnly(22, 0), new TimeOnly(5, 0), DayOfWeek.Monday, DayOfWeek.Tuesday);
        DailyUsageRestriction[] before = viewModel.WeekdayRestrictions.Select(day => day.ToRestriction()).ToArray();
        var bulk = viewModel.BulkScheduleEditor;
        bulk.StartTime.HourText = startHour;
        bulk.StartTime.MinuteText = startMinute;
        bulk.ReleaseTime.HourText = releaseHour;
        bulk.ReleaseTime.MinuteText = releaseMinute;

        Assert.IsFalse(viewModel.ApplyBulkScheduleCommand.CanExecute(null));
        Assert.IsFalse(string.IsNullOrWhiteSpace(bulk.ValidationMessage));
        viewModel.ApplyBulkScheduleCommand.Execute(null);

        CollectionAssert.AreEqual(before, viewModel.WeekdayRestrictions.Select(day => day.ToRestriction()).ToArray());
        Assert.AreEqual(0, store.SaveCount);
    }

    [STATestMethod]
    public async Task NoTargetsCannotApplyAndSelectionCommandsUpdateCountAndButtonText()
    {
        using var runtime = CreateRuntime(new RecordingStore(UsagePolicySettings.Default));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        viewModel.SelectBulkScheduleCommand.Execute(null);
        var bulk = viewModel.BulkScheduleEditor;
        bulk.StartTime.Set(new TimeOnly(22, 0));
        bulk.ReleaseTime.Set(new TimeOnly(5, 0));
        Assert.IsTrue(bulk.ActivateAfterApply);
        Assert.AreEqual(0, bulk.SelectedDayCount);
        Assert.IsFalse(viewModel.ApplyBulkScheduleCommand.CanExecute(null));
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        Assert.IsFalse(viewModel.HasWeeklyScheduleChanges);

        bulk.SelectAllCommand.Execute(null);
        Assert.AreEqual(7, bulk.SelectedDayCount);
        Assert.AreEqual("7개 요일에 시간 반영", bulk.ApplyButtonText);
        Assert.IsTrue(bulk.Targets.All(target => target.IsSelected));
        bulk.ClearSelectionCommand.Execute(null);
        Assert.AreEqual(0, bulk.SelectedDayCount);
        Assert.IsTrue(bulk.Targets.All(target => !target.IsSelected));
        Assert.IsFalse(viewModel.ApplyBulkScheduleCommand.CanExecute(null));
    }

    [STATestMethod]
    public async Task OvernightSundayDraftNamesMondayAndPendingSummaryIncludesAllChangedDays()
    {
        using var runtime = CreateRuntime(new RecordingStore(UsagePolicySettings.Default));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        ConfigureBulk(viewModel, new TimeOnly(23, 0), new TimeOnly(5, 0), DayOfWeek.Sunday, DayOfWeek.Wednesday);
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        Assert.AreEqual("월요일", Day(viewModel, DayOfWeek.Sunday).EndDay);
        Assert.AreEqual("일요일 23:00 → 월요일 05:00 · 6시간", Day(viewModel, DayOfWeek.Sunday).DraftSummary);
        Day(viewModel, DayOfWeek.Wednesday).StartTime.Set(new TimeOnly(19, 0));
        Day(viewModel, DayOfWeek.Wednesday).ReleaseTime.Set(new TimeOnly(21, 0));

        await viewModel.SaveWeeklyScheduleAsync();

        Assert.IsTrue(viewModel.IsWeeklyScheduleConfirmationVisible);
        Assert.Contains("일요일", viewModel.WeeklyScheduleConfirmationSummary);
        Assert.Contains("월요일 05:00", viewModel.WeeklyScheduleConfirmationSummary);
        Assert.Contains("수요일", viewModel.WeeklyScheduleConfirmationSummary);
    }

    [STATestMethod]
    public async Task RevertOnlyRestoresTheSelectedDayFromTheLatestSave()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        using var runtime = CreateRuntime(store);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        ConfigureBulk(viewModel, new TimeOnly(22, 0), new TimeOnly(23, 0), DayOfWeek.Monday, DayOfWeek.Tuesday);
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        await viewModel.SaveWeeklyScheduleAsync();
        ConfigureBulk(viewModel, new TimeOnly(23, 0), new TimeOnly(5, 0), DayOfWeek.Monday, DayOfWeek.Tuesday);
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        viewModel.SelectedWeekday = Day(viewModel, DayOfWeek.Monday);

        viewModel.RevertWeekdayCommand.Execute(null);

        AssertDraft(Day(viewModel, DayOfWeek.Monday), true, new TimeOnly(22, 0), new TimeOnly(23, 0));
        Assert.IsFalse(Day(viewModel, DayOfWeek.Monday).HasChanges);
        AssertDraft(Day(viewModel, DayOfWeek.Tuesday), true, new TimeOnly(23, 0), new TimeOnly(5, 0));
        Assert.AreEqual(1, store.SaveCount);
    }

    [STATestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ResetAndDiscardRestoreSavedDaysAndClearGroupTargets(bool useDiscardCommand)
    {
        var schedule = WeeklyUsageRestrictionSchedule.Default.WithRestriction(DayOfWeek.Monday,
            new DailyUsageRestriction(true, new TimeOnly(9, 0), new TimeOnly(10, 0)));
        var store = new RecordingStore(UsagePolicySettings.Default.WithWeeklySchedule(schedule));
        using var runtime = CreateRuntime(store);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        ConfigureBulk(viewModel, new TimeOnly(23, 0), new TimeOnly(5, 0), DayOfWeek.Monday, DayOfWeek.Sunday);
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        Day(viewModel, DayOfWeek.Friday).StartTime.HourText = string.Empty;

        if (useDiscardCommand)
        {
            viewModel.DiscardWeeklyScheduleCommand.Execute(null);
        }
        else
        {
            viewModel.ResetWeeklyScheduleEdits();
        }

        foreach (DailyUsageRestrictionViewModel day in viewModel.WeekdayRestrictions)
        {
            Assert.AreEqual(schedule.GetRestriction(day.DayOfWeek), day.ToRestriction());
        }
        Assert.AreEqual(0, viewModel.BulkScheduleEditor.SelectedDayCount);
        Assert.IsFalse(viewModel.HasWeeklyScheduleChanges);
        Assert.AreEqual(0, store.SaveCount);
    }

    [STATestMethod]
    public async Task ApplyDoesNotSaveAnImmediateLockAndChangedApplicationCancelsPendingConfirmation()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, lockPort: lockPort);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        ConfigureBulk(viewModel, new TimeOnly(19, 0), new TimeOnly(21, 0), DayOfWeek.Wednesday);
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        Assert.IsTrue(viewModel.WillLockImmediately);
        Assert.IsFalse(viewModel.IsWeeklyScheduleConfirmationVisible);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.LockRequired);
        Assert.AreEqual(0, store.SaveCount);
        await viewModel.SaveWeeklyScheduleAsync();
        Assert.IsTrue(viewModel.IsWeeklyScheduleConfirmationVisible);
        Assert.AreEqual(0, store.SaveCount);

        viewModel.BulkScheduleEditor.StartTime.Set(new TimeOnly(19, 30));
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        Assert.IsFalse(viewModel.IsWeeklyScheduleConfirmationVisible);
        await viewModel.ConfirmWeeklyScheduleAsync();
        Assert.AreEqual(0, store.SaveCount);
        await viewModel.SaveWeeklyScheduleAsync();
        Assert.AreEqual(1, store.SaveCount);
        Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.LockRequired);
    }

    [STATestMethod]
    public async Task ImmediateLockRequiresTheExistingSaveAndConfirmationActions()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        using var runtime = CreateRuntime(store);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        ConfigureBulk(viewModel, new TimeOnly(19, 0), new TimeOnly(21, 0), DayOfWeek.Wednesday);
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        await viewModel.SaveWeeklyScheduleAsync();
        Assert.AreEqual(0, store.SaveCount);

        await viewModel.ConfirmWeeklyScheduleAsync();

        Assert.AreEqual(1, store.SaveCount);
        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        Assert.AreEqual(new TimeOnly(19, 0), store.Settings.WeeklySchedule.Wednesday.StartTime);
    }

    [STATestMethod]
    public async Task LockStatePreventsGroupApplicationWithoutChangingDrafts()
    {
        var clock = new ManualTimeProvider();
        var schedule = WeeklyUsageRestrictionSchedule.Default.WithRestriction(DayOfWeek.Wednesday,
            new DailyUsageRestriction(true, new TimeOnly(19, 30), new TimeOnly(21, 0)));
        var store = new RecordingStore(UsagePolicySettings.Default.WithWeeklySchedule(schedule));
        using var runtime = CreateRuntime(store, clock);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        ConfigureBulk(viewModel, new TimeOnly(22, 0), new TimeOnly(23, 0), DayOfWeek.Monday);
        DailyUsageRestriction before = Day(viewModel, DayOfWeek.Monday).ToRestriction();
        clock.UtcNow = new DateTimeOffset(2026, 9, 23, 19, 30, 0, TimeSpan.Zero);
        await runtime.RefreshAsync();

        Assert.IsFalse(viewModel.ApplyBulkScheduleCommand.CanExecute(null));
        Assert.IsFalse(viewModel.SelectBulkScheduleCommand.CanExecute(null));
        Assert.IsFalse(viewModel.CanEditWeeklySchedule);
        viewModel.ApplyBulkScheduleCommand.Execute(null);

        Assert.AreEqual(before, Day(viewModel, DayOfWeek.Monday).ToRestriction());
        Assert.AreEqual(0, store.SaveCount);
    }

    [STATestMethod]
    public async Task SaveInProgressPreventsApplicationUntilTheSaveCompletes()
    {
        var store = new RecordingStore(UsagePolicySettings.Default) { HoldWrites = true };
        using var runtime = CreateRuntime(store);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        ConfigureBulk(viewModel, new TimeOnly(22, 0), new TimeOnly(23, 0), DayOfWeek.Monday);
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        viewModel.BulkScheduleEditor.StartTime.Set(new TimeOnly(21, 0));
        Task saving = viewModel.SaveWeeklyScheduleAsync();
        try
        {
            await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(viewModel.ApplyBulkScheduleCommand.CanExecute(null));
            Assert.IsFalse(viewModel.CanEditWeeklySchedule);
            viewModel.ApplyBulkScheduleCommand.Execute(null);
            Assert.AreEqual(new TimeOnly(22, 0), Day(viewModel, DayOfWeek.Monday).StartTime.ToTimeOnly());
        }
        finally
        {
            store.ReleaseSave.TrySetResult(true);
            await saving;
        }
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(new TimeOnly(22, 0), store.Settings.WeeklySchedule.Monday.StartTime);
    }

    private static DailyUsageRestrictionViewModel Day(MainWindowViewModel viewModel, DayOfWeek day) =>
        viewModel.WeekdayRestrictions.Single(editor => editor.DayOfWeek == day);

    private static void ConfigureBulk(MainWindowViewModel viewModel, TimeOnly start, TimeOnly release, params DayOfWeek[] days)
    {
        viewModel.SelectBulkScheduleCommand.Execute(null);
        var bulk = viewModel.BulkScheduleEditor;
        bulk.ClearSelectionCommand.Execute(null);
        foreach (var target in bulk.Targets)
        {
            target.IsSelected = days.Contains(target.DayOfWeek);
        }
        bulk.StartTime.Set(start);
        bulk.ReleaseTime.Set(release);
    }

    private static void AssertDraft(DailyUsageRestrictionViewModel day, bool enabled, TimeOnly start, TimeOnly release) =>
        Assert.AreEqual(new DailyUsageRestriction(enabled, start, release), day.ToRestriction());

    private static MainWindowViewModel CreateViewModel(UsagePolicyRuntime runtime) => new(
        runtime, restartOnExitWhenUnlocked: true, saveExecutionSettings: (_, _) => Task.CompletedTask
#if DEBUG
        , requestLock: () => Task.CompletedTask, requestDevelopmentUnlock: () => Task.CompletedTask
#endif
        );

    private static UsagePolicyRuntime CreateRuntime(RecordingStore store, TimeProvider? clock = null, RecordingLockPort? lockPort = null) =>
        new(store, lockPort ?? new RecordingLockPort(), new EmptyPromptCatalog(), clock ?? new ManualTimeProvider());

    private sealed class RecordingStore(UsagePolicySettings settings) : IUsagePolicySettingsStore
    {
        internal UsagePolicySettings Settings { get; private set; } = settings;
        internal int SaveCount { get; private set; }
        internal bool HoldWrites { get; init; }
        internal TaskCompletionSource<bool> SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UsagePolicySettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
        public async Task SaveAsync(UsagePolicySettings value, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            SaveEntered.TrySetResult(true);
            if (HoldWrites)
            {
                await ReleaseSave.Task.WaitAsync(cancellationToken);
            }
            Settings = value;
        }
    }

    private sealed class RecordingLockPort : IUsagePolicyLockPort
    {
        internal List<bool> AppliedRequirements { get; } = [];
        public Task ApplyPolicyLockRequirementAsync(bool lockRequired, bool lockRequiredAfterRestart, CancellationToken cancellationToken = default)
        {
            AppliedRequirements.Add(lockRequired);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyPromptCatalog : IEmergencyUnlockPromptCatalog
    {
        public Task<IReadOnlyList<string>> SelectDistinctAsync(int count, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = new(2026, 9, 23, 19, 26, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
