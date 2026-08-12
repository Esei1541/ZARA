using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class MainWindowViewModelTests
{
    private static readonly bool[] FirstLockEndsThenSecondLockStarts = [true, false, true];

    [STATestMethod]
    public async Task RuntimeStateChangeDoesNotOverwriteUnsavedScheduleText()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero));
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new TimeOnly(9, 0),
            new TimeOnly(10, 0));
        var store = new RecordingStore(settings);
        using var runtime = CreateRuntime(store, timeProvider);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        DailyUsageRestrictionViewModel monday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Monday);
        monday.StartTime.HourText = "8";
        monday.StartTime.MinuteText = "30";

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();

        Assert.IsFalse(viewModel.CanChangeSettings);
        Assert.AreEqual("8", monday.StartTime.HourText);
        Assert.AreEqual("30", monday.StartTime.MinuteText);
    }

    [STATestMethod]
    public async Task EditingAfterFirstRestrictionEndsSavesAndLocksAtTheSecondStart()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(SettingsWithMondayRestriction(
            new TimeOnly(9, 0),
            new TimeOnly(9, 1)));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, timeProvider, lockPort);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 9, 1, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();

        DailyUsageRestrictionViewModel monday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Monday);
        monday.StartTime.Set(new TimeOnly(9, 2));
        monday.ReleaseTime.Set(new TimeOnly(10, 0));
        var notificationSource = new TaskCompletionSource<MainWindowNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.NotificationRequested += (_, notification) =>
            notificationSource.TrySetResult(notification);

        viewModel.SaveUsagePolicySettingsCommand.Execute(parameter: null);
        MainWindowNotificationEventArgs saveResult = await notificationSource.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.IsFalse(saveResult.IsError);
        Assert.AreEqual(new TimeOnly(9, 2), store.Settings.WeeklySchedule.Monday.StartTime);
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 9, 2, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();

        Assert.IsTrue(runtime.CurrentSnapshot.Evaluation.LockRequired);
        CollectionAssert.AreEqual(
            FirstLockEndsThenSecondLockStarts,
            lockPort.AppliedRequirements);
    }

    [STATestMethod]
    public async Task RuntimeTimeRefreshUpdatesTheNextLockCountdown()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero));
        using var runtime = CreateRuntime(
            new RecordingStore(SettingsWithMondayRestriction(
                new TimeOnly(9, 0),
                new TimeOnly(10, 0))),
            timeProvider);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        Assert.AreEqual("다음 잠금까지 01:00:00 남았습니다.", viewModel.UsagePolicyStatusMessage);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 8, 0, 1, TimeSpan.Zero));
        await runtime.RefreshAsync();

        Assert.AreEqual("다음 잠금까지 00:59:59 남았습니다.", viewModel.UsagePolicyStatusMessage);
    }

    [STATestMethod]
    public async Task DisabledDefaultScheduleReportsThatNoUsageBanTimeIsConfigured()
    {
        using var runtime = CreateRuntime(
            new RecordingStore(UsagePolicySettings.Default),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        Assert.AreEqual(
            "지금은 설정된 사용 금지 시각이 없습니다.",
            viewModel.UsagePolicyStatusMessage);
    }

    [STATestMethod]
    public async Task ReservationPresentationUsesTheRuntimeEvaluationTime()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 8, 10),
            new TimeOnly(8, 0),
            new TimeOnly(8, 1),
            "시험");
        using var runtime = CreateRuntime(
            new RecordingStore(new UsagePolicySettings(
                WeeklyUsageRestrictionSchedule.Default,
                EmergencyUnlockSettings.Default,
                [reservation])),
            timeProvider);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        Assert.IsTrue(viewModel.Reservations.Single().IsActive);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 8, 1, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();

        Assert.IsFalse(viewModel.Reservations.Single().IsActive);
    }

    [STATestMethod]
    public async Task RepeatedSuccessfulSavesRaiseFreshNotificationsEveryTime()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        using var runtime = CreateRuntime(
            store,
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        var notifications = new List<MainWindowNotificationEventArgs>();
        viewModel.NotificationRequested += (_, notification) =>
            notifications.Add(notification);

        await viewModel.SaveUsagePolicySettingsAsync();
        await viewModel.SaveUsagePolicySettingsAsync();

        Assert.AreEqual(2, store.SaveCount);
        Assert.HasCount(2, notifications);
        Assert.IsTrue(notifications.All(notification => !notification.IsError));
        Assert.IsTrue(notifications.All(notification => notification.Title == "설정 저장"));
        Assert.IsTrue(notifications.All(notification =>
            notification.Message == "사용 금지 시각과 긴급 해제 설정을 저장했습니다."));
    }

    [STATestMethod]
    public async Task SavedSettingsWithFailedLockApplicationReportPartialSuccess()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(
            store,
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)),
            lockPort);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        DailyUsageRestrictionViewModel monday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Monday);
        monday.IsEnabled = true;
        monday.StartTime.Set(new TimeOnly(8, 0));
        monday.ReleaseTime.Set(new TimeOnly(9, 0));
        lockPort.FailWhenLocking = true;
        var notificationSource = new TaskCompletionSource<MainWindowNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.NotificationRequested += (_, notification) =>
            notificationSource.TrySetResult(notification);

        viewModel.SaveUsagePolicySettingsCommand.Execute(parameter: null);
        MainWindowNotificationEventArgs result = await notificationSource.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.IsTrue(result.IsError);
        Assert.AreEqual("설정 저장", result.Title);
        Assert.AreEqual(
            "설정은 저장했습니다. 현재 잠금 상태를 적용하지 못해 자동으로 다시 시도합니다.",
            result.Message);
        Assert.AreEqual(1, store.SaveCount);
        Assert.IsTrue(runtime.CurrentSnapshot.Settings.WeeklySchedule.Monday.IsEnabled);
    }

    [STATestMethod]
    public async Task LockDemoFailureUsesItsOwnPopupTitleAndMessage()
    {
        using var runtime = CreateRuntime(
            new RecordingStore(UsagePolicySettings.Default),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = new MainWindowViewModel(
            runtime,
            restartOnExitWhenUnlocked: true,
            updateRestartSetting: _ => Task.CompletedTask,
            requestLock: () => Task.FromException(new InvalidOperationException("Failed.")));
        var notificationSource = new TaskCompletionSource<MainWindowNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.NotificationRequested += (_, notification) =>
            notificationSource.TrySetResult(notification);

        viewModel.StartLockDemoCommand.Execute(parameter: null);
        MainWindowNotificationEventArgs result = await notificationSource.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.IsTrue(result.IsError);
        Assert.AreEqual("잠금 화면 시연", result.Title);
        Assert.AreEqual(
            "잠금 화면을 열지 못했습니다. 잠시 후 다시 시도하세요.",
            result.Message);
    }

    [STATestMethod]
    public async Task InvalidEnabledMidnightIntervalRaisesKoreanPopupResult()
    {
        using var runtime = CreateRuntime(
            new RecordingStore(UsagePolicySettings.Default),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        DailyUsageRestrictionViewModel monday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Monday);
        monday.IsEnabled = true;
        var notificationSource = new TaskCompletionSource<MainWindowNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.NotificationRequested += (_, notification) =>
            notificationSource.TrySetResult(notification);

        viewModel.SaveUsagePolicySettingsCommand.Execute(parameter: null);
        MainWindowNotificationEventArgs result = await notificationSource.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(
            "월요일의 시작 시각과 해제 시각은 다르게 입력하세요.",
            result.Message);
    }

    private static MainWindowViewModel CreateViewModel(UsagePolicyRuntime runtime) =>
        new(
            runtime,
            restartOnExitWhenUnlocked: true,
            updateRestartSetting: _ => Task.CompletedTask,
            requestLock: () => Task.CompletedTask);

    private static UsagePolicyRuntime CreateRuntime(
        RecordingStore store,
        TimeProvider timeProvider,
        RecordingLockPort? lockPort = null) =>
        new(store, lockPort ?? new RecordingLockPort(), new EmptyPromptCatalog(), timeProvider);

    private static UsagePolicySettings SettingsWithMondayRestriction(
        TimeOnly start,
        TimeOnly release) =>
        new(
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(
                DayOfWeek.Monday,
                new DailyUsageRestriction(true, start, release)),
            EmergencyUnlockSettings.Default,
            Array.Empty<OutOfHoursReservation>());

    private sealed class RecordingStore : IUsagePolicySettingsStore
    {
        private UsagePolicySettings _settings;

        internal RecordingStore(UsagePolicySettings settings)
        {
            _settings = settings;
        }

        internal int SaveCount { get; private set; }

        internal UsagePolicySettings Settings => _settings;

        public Task<UsagePolicySettings> LoadAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task SaveAsync(
            UsagePolicySettings settings,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLockPort : IUsagePolicyLockPort
    {
        internal bool FailWhenLocking { get; set; }

        internal List<bool> AppliedRequirements { get; } = [];

        public Task ApplyPolicyLockRequirementAsync(
            bool lockRequired,
            CancellationToken cancellationToken = default)
        {
            AppliedRequirements.Add(lockRequired);
            return lockRequired && FailWhenLocking
                ? Task.FromException(new InvalidOperationException("Lock failed."))
                : Task.CompletedTask;
        }
    }

    private sealed class EmptyPromptCatalog : IEmergencyUnlockPromptCatalog
    {
        public Task<IReadOnlyList<string>> SelectDistinctAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        internal ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
