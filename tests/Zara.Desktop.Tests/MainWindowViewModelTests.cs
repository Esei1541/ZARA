using System.IO;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class MainWindowViewModelTests
{
    private static readonly bool[] FirstLockEndsThenSecondLockStarts = [true, false, true];
    private static readonly int[] ReminderMinutes = [30, 10, 5, 1];

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

        viewModel.SaveWeeklyScheduleCommand.Execute(parameter: null);
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
    [DataRow(30)]
    [DataRow(10)]
    [DataRow(5)]
    [DataRow(1)]
    public async Task EachVoiceReminderCommandUpdatesOnlyItsOwnSetting(int minutes)
    {
        using var runtime = CreateRuntime(
            new RecordingStore(UsagePolicySettings.Default),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        int? savedMinutes = null;
        bool? savedValue = null;
        using var viewModel = CreateViewModel(
            runtime,
            LockReminderSettings.Default,
            (requestedMinutes, enabled) =>
            {
                savedMinutes = requestedMinutes;
                savedValue = enabled;
                return Task.CompletedTask;
            });
        var notificationSource = new TaskCompletionSource<MainWindowNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.NotificationRequested += (_, notification) =>
            notificationSource.TrySetResult(notification);

        GetVoiceReminderCommand(viewModel, minutes).Execute(parameter: null);
        MainWindowNotificationEventArgs notification = await notificationSource.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(minutes, savedMinutes);
        Assert.IsFalse(savedValue);
        Assert.IsFalse(GetVoiceReminderValue(viewModel, minutes));
        foreach (int otherMinutes in ReminderMinutes.Where(value => value != minutes))
        {
            Assert.IsTrue(GetVoiceReminderValue(viewModel, otherMinutes));
        }

        Assert.IsFalse(notification.IsError);
        Assert.AreEqual("잠금 전 음성 안내 설정을 저장했습니다.", notification.Message);
    }

    [STATestMethod]
    public async Task FailedVoiceReminderSaveRestoresTheAcknowledgedValue()
    {
        using var runtime = CreateRuntime(
            new RecordingStore(UsagePolicySettings.Default),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(
            runtime,
            LockReminderSettings.Default,
            (_, _) => Task.FromException(new IOException("Save failed.")));
        var notificationSource = new TaskCompletionSource<MainWindowNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.NotificationRequested += (_, notification) =>
            notificationSource.TrySetResult(notification);

        viewModel.ToggleVoiceReminder10MinutesCommand.Execute(parameter: null);
        MainWindowNotificationEventArgs notification = await notificationSource.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.IsTrue(viewModel.VoiceReminder10Minutes);
        Assert.IsTrue(notification.IsError);
        Assert.AreEqual("설정을 저장하지 못했습니다. 잠시 후 다시 시도하세요.", notification.Message);
    }

    [STATestMethod]
    public async Task VoiceReminderCommandsAreBlockedDuringUsageBan()
    {
        using var runtime = CreateRuntime(
            new RecordingStore(SettingsWithMondayRestriction(
                new TimeOnly(7, 0),
                new TimeOnly(10, 0))),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(
            runtime,
            LockReminderSettings.Default,
            (_, _) => Task.CompletedTask);

        Assert.IsFalse(viewModel.ToggleVoiceReminder30MinutesCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ToggleVoiceReminder10MinutesCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ToggleVoiceReminder5MinutesCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ToggleVoiceReminder1MinuteCommand.CanExecute(null));
    }

    [STATestMethod]
    public async Task VoiceReminderCommandsRemainDisabledWithoutAnUpdateCallback()
    {
        using var runtime = CreateRuntime(
            new RecordingStore(UsagePolicySettings.Default),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        Assert.IsTrue(viewModel.CanChangeSettings);
        Assert.IsFalse(viewModel.ToggleVoiceReminder30MinutesCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ToggleVoiceReminder10MinutesCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ToggleVoiceReminder5MinutesCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ToggleVoiceReminder1MinuteCommand.CanExecute(null));
    }

    [STATestMethod]
    public async Task SavingOneVoiceReminderBlocksTheOtherReminderCommands()
    {
        using var runtime = CreateRuntime(
            new RecordingStore(UsagePolicySettings.Default),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = CreateViewModel(
            runtime,
            LockReminderSettings.Default,
            async (_, _) =>
            {
                saveStarted.TrySetResult();
                await allowSave.Task;
            });
        var notificationSource = new TaskCompletionSource<MainWindowNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.NotificationRequested += (_, notification) =>
            notificationSource.TrySetResult(notification);

        viewModel.ToggleVoiceReminder30MinutesCommand.Execute(parameter: null);
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(viewModel.ToggleVoiceReminder10MinutesCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ToggleVoiceReminder5MinutesCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ToggleVoiceReminder1MinuteCommand.CanExecute(null));

        allowSave.TrySetResult();
        await notificationSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(viewModel.ToggleVoiceReminder10MinutesCommand.CanExecute(null));
    }

    [STATestMethod]
    public async Task SavingEveryWeekdayAsDisabledRemovesTheNextLockCountdown()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 12, 23, 30, 0, TimeSpan.Zero));
        var store = new RecordingStore(new UsagePolicySettings(
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(
                DayOfWeek.Wednesday,
                new DailyUsageRestriction(
                    isEnabled: true,
                    startTime: new TimeOnly(0, 0),
                    releaseTime: new TimeOnly(23, 28))),
            EmergencyUnlockSettings.Default,
            Array.Empty<OutOfHoursReservation>()));
        using var runtime = CreateRuntime(store, timeProvider);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        Assert.StartsWith("다음 잠금까지 ", viewModel.UsagePolicyStatusMessage);

        foreach (DailyUsageRestrictionViewModel weekday in viewModel.WeekdayRestrictions)
        {
            weekday.IsRestrictionEnabled = false;
        }

        await viewModel.SaveWeeklyScheduleAsync();

        Assert.IsTrue(AllWeekdaysAreDisabled(store.Settings.WeeklySchedule));
        Assert.AreEqual(
            "지금은 설정된 사용 금지 시각이 없습니다.",
            viewModel.UsagePolicyStatusMessage);
    }

#if DEBUG
    [STATestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DevelopmentResetUnchecksTheWeekdaysAndReenablesSettings(bool restrictionEnabled)
    {
        var settings = SettingsWithMondayRestriction(new TimeOnly(9, 0), new TimeOnly(10, 0))
            .WithEmergencyUnlock(new EmergencyUnlockSettings(17, 0));
        if (!restrictionEnabled)
        {
            settings = settings.WithWeeklySchedule(settings.WeeklySchedule.WithRestriction(
                DayOfWeek.Monday,
                new DailyUsageRestriction(false, new TimeOnly(9, 0), new TimeOnly(10, 0))));
        }

        using var runtime = CreateRuntime(
            new RecordingStore(settings),
            new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 9, 30, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        if (restrictionEnabled)
        {
            await runtime.StartEmergencyUnlockAsync();
        }

        using var viewModel = CreateViewModel(runtime);
        Assert.AreEqual(!restrictionEnabled, viewModel.CanChangeUsagePolicySettings);
        if (!restrictionEnabled)
        {
            viewModel.WeekdayRestrictions.Single(day => day.DayOfWeek == DayOfWeek.Monday)
                .IsRestrictionEnabled = true;
        }

        await runtime.DisableWeeklyScheduleForDevelopmentAsync();
        viewModel.ResetWeeklyScheduleEdits();

        Assert.IsTrue(viewModel.WeekdayRestrictions.All(day => !day.IsRestrictionEnabled));
        DailyUsageRestrictionViewModel monday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Monday);
        Assert.AreEqual("9", monday.StartTime.HourText);
        Assert.AreEqual("10", monday.ReleaseTime.HourText);
        Assert.AreEqual("17", viewModel.EmergencyDurationMinutesText);
        Assert.IsTrue(viewModel.CanChangeSettings);
        Assert.IsTrue(viewModel.SaveWeeklyScheduleCommand.CanExecute(parameter: null));
        Assert.IsTrue(viewModel.SaveEmergencyUnlockSettingsCommand.CanExecute(parameter: null));
        Assert.IsTrue(viewModel.ToggleRestartSettingCommand.CanExecute(parameter: null));
    }

#endif

    [STATestMethod]
    public async Task ReservationExpiresFromTheListAtItsEndTime()
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

        Assert.IsEmpty(viewModel.Reservations);
    }

    [STATestMethod]
    public async Task InitializeRemovesExpiredReservationsFromTheList()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 15, 0, 0, TimeSpan.Zero));
        var expiredReservation = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 22),
            new TimeOnly(0, 0),
            new TimeOnly(1, 0),
            "지난 예약");
        var futureReservation = new OutOfHoursReservation(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 22),
            new TimeOnly(16, 0),
            new TimeOnly(17, 0),
            "미래 예약");
        var store = new RecordingStore(
            UsagePolicySettings.Default.WithReservations([expiredReservation, futureReservation]));
        using var runtime = CreateRuntime(store, timeProvider);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        Assert.HasCount(1, viewModel.Reservations);
        Assert.AreEqual(futureReservation.Id, viewModel.Reservations.Single().Id);
        Assert.HasCount(1, store.Settings.Reservations);
        Assert.AreEqual(futureReservation.Id, store.Settings.Reservations.Single().Id);

        await runtime.RefreshAsync();

        Assert.HasCount(1, viewModel.Reservations);
        Assert.AreEqual(futureReservation.Id, viewModel.Reservations.Single().Id);
        Assert.HasCount(1, store.Settings.Reservations);
        Assert.AreEqual(futureReservation.Id, store.Settings.Reservations.Single().Id);
    }

    [STATestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    public async Task AnyReservationCanBeDeletedWithoutEnablingOtherSettings(bool withinUsageBan, int dayOffset)
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero));
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 10).AddDays(dayOffset),
            new TimeOnly(8, 0), new TimeOnly(9, 0), "삭제 대상");
        var other = new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 8, 12), new TimeOnly(8, 0), new TimeOnly(9, 0), "유지");
        UsagePolicySettings settings = withinUsageBan
            ? SettingsWithMondayRestriction(new TimeOnly(7, 0), new TimeOnly(10, 0))
            : UsagePolicySettings.Default;
        var store = new RecordingStore(settings.WithReservations([reservation, other]));
        var lockPort = new RecordingLockPort();
        using var runtime = CreateRuntime(store, timeProvider, lockPort);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        Assert.AreEqual(!withinUsageBan, viewModel.CanChangeUsagePolicySettings);

        ReservationChangeStatus result = await viewModel.RemoveReservationAsync(reservation.Id);

        Assert.AreEqual(ReservationChangeStatus.Removed, result);
        Assert.AreEqual(other.Id, viewModel.Reservations.Single().Id);
        Assert.AreEqual(other.Id, store.Settings.Reservations.Single().Id);
        Assert.AreEqual(withinUsageBan, lockPort.AppliedRequirements.Last());
        Assert.AreEqual(!withinUsageBan, viewModel.CanChangeUsagePolicySettings);
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

        await viewModel.SaveWeeklyScheduleAsync();
        await viewModel.SaveWeeklyScheduleAsync();

        Assert.AreEqual(2, store.SaveCount);
        Assert.HasCount(2, notifications);
        Assert.IsTrue(notifications.All(notification => !notification.IsError));
        Assert.IsTrue(notifications.All(notification => notification.Title == "사용 금지 시각"));
        Assert.IsTrue(notifications.All(notification =>
            notification.Message == "사용 금지 시각을 저장했습니다."));
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
        monday.IsRestrictionEnabled = true;
        monday.StartTime.Set(new TimeOnly(8, 0));
        monday.ReleaseTime.Set(new TimeOnly(9, 0));
        lockPort.FailWhenLocking = true;
        var notificationSource = new TaskCompletionSource<MainWindowNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.NotificationRequested += (_, notification) =>
            notificationSource.TrySetResult(notification);

        viewModel.SaveWeeklyScheduleCommand.Execute(parameter: null);
        MainWindowNotificationEventArgs result = await notificationSource.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.IsTrue(result.IsError);
        Assert.AreEqual("사용 금지 시각", result.Title);
        Assert.AreEqual(
            "설정은 저장했습니다. 현재 잠금 상태를 적용하지 못해 자동으로 다시 시도합니다.",
            result.Message);
        Assert.AreEqual(1, store.SaveCount);
        Assert.IsTrue(runtime.CurrentSnapshot.Settings.WeeklySchedule.Monday.IsEnabled);
    }

#if DEBUG
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
            updateRestartSetting: _ => Task.CompletedTask
#if DEBUG
            , requestLock: () => Task.FromException(new InvalidOperationException("Failed.")),
            requestDevelopmentUnlock: () => Task.CompletedTask
#endif
            );
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

#endif

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
        monday.IsRestrictionEnabled = true;
        var notificationSource = new TaskCompletionSource<MainWindowNotificationEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.NotificationRequested += (_, notification) =>
            notificationSource.TrySetResult(notification);

        viewModel.SaveWeeklyScheduleCommand.Execute(parameter: null);
        MainWindowNotificationEventArgs result = await notificationSource.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(
            "월요일의 시작 시각과 해제 시각은 다르게 입력하세요.",
            result.Message);
    }

    [STATestMethod]
    public async Task SavingWeeklyScheduleDoesNotReadOrOverwriteEmergencyEdits()
    {
        var originalEmergency = new EmergencyUnlockSettings(durationMinutes: 17, sentenceCount: 4);
        var store = new RecordingStore(new UsagePolicySettings(
            WeeklyUsageRestrictionSchedule.Default,
            originalEmergency,
            Array.Empty<OutOfHoursReservation>()));
        using var runtime = CreateRuntime(
            store,
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        DailyUsageRestrictionViewModel monday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Monday);
        monday.IsRestrictionEnabled = true;
        monday.StartTime.Set(new TimeOnly(9, 0));
        monday.ReleaseTime.Set(new TimeOnly(10, 0));
        viewModel.EmergencyDurationMinutesText = "문자";

        await viewModel.SaveWeeklyScheduleAsync();

        Assert.IsTrue(store.Settings.WeeklySchedule.Monday.IsEnabled);
        Assert.AreEqual(originalEmergency, store.Settings.EmergencyUnlock);
    }

    [STATestMethod]
    public async Task SavingEmergencySettingsDoesNotReadOrOverwriteScheduleEdits()
    {
        WeeklyUsageRestrictionSchedule originalSchedule =
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(
                DayOfWeek.Tuesday,
                new DailyUsageRestriction(
                    isEnabled: true,
                    startTime: new TimeOnly(9, 0),
                    releaseTime: new TimeOnly(10, 0)));
        var store = new RecordingStore(new UsagePolicySettings(
            originalSchedule,
            EmergencyUnlockSettings.Default,
            Array.Empty<OutOfHoursReservation>()));
        using var runtime = CreateRuntime(
            store,
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        DailyUsageRestrictionViewModel monday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Monday);
        monday.IsRestrictionEnabled = true;
        viewModel.EmergencyDurationMinutesText = "25";
        viewModel.EmergencySentenceCountText = "8";

        await viewModel.SaveEmergencyUnlockSettingsAsync();

        Assert.AreEqual(originalSchedule, store.Settings.WeeklySchedule);
        Assert.AreEqual(new EmergencyUnlockSettings(25, 8), store.Settings.EmergencyUnlock);
    }

    [STATestMethod]
    public async Task WeeklyEmergencyLimitSettingsUseSundayFirstAndSaveIndependentlyOfScheduleEdits()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        using var runtime = CreateRuntime(
            store,
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        CollectionAssert.AreEqual(
            new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday,
                DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
                DayOfWeek.Saturday },
            viewModel.EmergencyWeeklyResetDays.Select(day => day.Key).ToArray());
        Assert.AreEqual(DayOfWeek.Sunday, viewModel.EmergencyWeeklyResetDay);

        viewModel.WeekdayRestrictions.Single(day => day.DayOfWeek == DayOfWeek.Monday)
            .IsRestrictionEnabled = true;
        viewModel.EmergencyWeeklyLimitEnabled = true;
        viewModel.EmergencyWeeklyResetDay = DayOfWeek.Friday;
        viewModel.EmergencyWeeklyMaximumCountText = "7";

        await viewModel.SaveEmergencyUnlockSettingsAsync();

        Assert.IsTrue(store.Settings.EmergencyUnlock.WeeklyLimitEnabled);
        Assert.AreEqual(DayOfWeek.Friday, store.Settings.EmergencyUnlock.WeeklyResetDay);
        Assert.AreEqual(7, store.Settings.EmergencyUnlock.WeeklyMaximumCount);
        Assert.IsFalse(store.Settings.WeeklySchedule.Monday.IsEnabled);
    }

    [STATestMethod]
    public async Task DisabledWeeklyEmergencyLimitIgnoresInvalidEditorsAndRestoresSavedValues()
    {
        var storedEmergency = new EmergencyUnlockSettings(
            durationMinutes: 10,
            sentenceCount: 3,
            weeklyLimitEnabled: true,
            weeklyResetDay: DayOfWeek.Tuesday,
            weeklyMaximumCount: 5);
        var store = new RecordingStore(UsagePolicySettings.Default.WithEmergencyUnlock(
            storedEmergency));
        using var runtime = CreateRuntime(
            store,
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        viewModel.EmergencyWeeklyLimitEnabled = false;
        viewModel.EmergencyWeeklyResetDay = null;
        viewModel.EmergencyWeeklyMaximumCountText = "invalid";
        await viewModel.SaveEmergencyUnlockSettingsAsync();

        Assert.IsFalse(store.Settings.EmergencyUnlock.WeeklyLimitEnabled);
        Assert.AreEqual(DayOfWeek.Tuesday, store.Settings.EmergencyUnlock.WeeklyResetDay);
        Assert.AreEqual(5, store.Settings.EmergencyUnlock.WeeklyMaximumCount);
        Assert.AreEqual(DayOfWeek.Tuesday, viewModel.EmergencyWeeklyResetDay);
        Assert.AreEqual("5", viewModel.EmergencyWeeklyMaximumCountText);
    }

    [STATestMethod]
    public async Task WeeklyEmergencyLimitShowsRemainingCountInCountdownAndLockedStatus()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero));
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new TimeOnly(9, 0),
            new TimeOnly(10, 0)).WithEmergencyUnlock(new EmergencyUnlockSettings(
                durationMinutes: 10,
                sentenceCount: 3,
                weeklyLimitEnabled: true,
                weeklyResetDay: DayOfWeek.Sunday,
                weeklyMaximumCount: 2));
        using var runtime = CreateRuntime(new RecordingStore(settings), timeProvider);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        Assert.AreEqual(
            "다음 잠금까지 01:00:00 남았습니다. (남은 긴급 해제 2회)",
            viewModel.UsagePolicyStatusMessage);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();

        Assert.AreEqual(
            "현재는 사용 금지 시간입니다. 설정을 바꿀 수 없습니다. (남은 긴급 해제 2회)",
            viewModel.UsagePolicyStatusMessage);
    }

    [STATestMethod]
    [DataRow("")]
    [DataRow("0")]
    [DataRow("100")]
    [DataRow("invalid")]
    public async Task EnabledWeeklyLimitRejectsInvalidMaximumCountWithSpecificMessage(string input)
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        using var runtime = CreateRuntime(
            store,
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        viewModel.EmergencyWeeklyLimitEnabled = true;
        viewModel.EmergencyWeeklyMaximumCountText = input;

        ArgumentException exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => viewModel.SaveEmergencyUnlockSettingsAsync());

        Assert.AreEqual("주간 최대 횟수는 1~99 사이의 숫자로 입력하세요.", exception.Message);
        Assert.AreEqual(EmergencyUnlockSettings.Default, store.Settings.EmergencyUnlock);
    }

    [STATestMethod]
    public async Task EnabledWeeklyLimitRequiresAResetDay()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        using var runtime = CreateRuntime(
            store,
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        viewModel.EmergencyWeeklyLimitEnabled = true;
        viewModel.EmergencyWeeklyResetDay = null;

        ArgumentException exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => viewModel.SaveEmergencyUnlockSettingsAsync());

        Assert.AreEqual("초기화 요일을 선택하세요.", exception.Message);
        Assert.AreEqual(EmergencyUnlockSettings.Default, store.Settings.EmergencyUnlock);
    }

    [STATestMethod]
    public async Task WeeklyLimitEditsSurviveRefreshUntilResetRestoresSavedValues()
    {
        var saved = new EmergencyUnlockSettings(
            durationMinutes: 10,
            sentenceCount: 3,
            weeklyLimitEnabled: true,
            weeklyResetDay: DayOfWeek.Tuesday,
            weeklyMaximumCount: 5);
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero));
        using var runtime = CreateRuntime(
            new RecordingStore(UsagePolicySettings.Default.WithEmergencyUnlock(saved)),
            timeProvider);
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        viewModel.EmergencyWeeklyLimitEnabled = false;
        viewModel.EmergencyWeeklyResetDay = DayOfWeek.Friday;
        viewModel.EmergencyWeeklyMaximumCountText = "7";

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 10, 8, 1, 0, TimeSpan.Zero));
        await runtime.RefreshAsync();

        Assert.IsFalse(viewModel.EmergencyWeeklyLimitEnabled);
        Assert.AreEqual(DayOfWeek.Friday, viewModel.EmergencyWeeklyResetDay);
        Assert.AreEqual("7", viewModel.EmergencyWeeklyMaximumCountText);

        viewModel.ResetEmergencyUnlockEdits();

        Assert.IsTrue(viewModel.EmergencyWeeklyLimitEnabled);
        Assert.AreEqual(DayOfWeek.Tuesday, viewModel.EmergencyWeeklyResetDay);
        Assert.AreEqual("5", viewModel.EmergencyWeeklyMaximumCountText);
    }

    [STATestMethod]
    public async Task LastEmergencyUnlockShowsZeroRemainingWhileUnlockIsActive()
    {
        UsagePolicySettings settings = SettingsWithMondayRestriction(
            new TimeOnly(9, 0),
            new TimeOnly(10, 0)).WithEmergencyUnlock(new EmergencyUnlockSettings(
                durationMinutes: 10,
                sentenceCount: 0,
                weeklyLimitEnabled: true,
                weeklyResetDay: DayOfWeek.Sunday,
                weeklyMaximumCount: 1));
        using var runtime = CreateRuntime(
            new RecordingStore(settings),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 9, 30, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);

        EmergencyUnlockStartResult started = await runtime.StartEmergencyUnlockAsync();

        Assert.IsTrue(started.StartedImmediately);
        Assert.AreEqual(0, runtime.CurrentSnapshot.EmergencyUnlockRemainingCount);
        Assert.AreEqual(
            "긴급 해제가 적용 중입니다. 사용 금지 시간이라 설정은 바꿀 수 없습니다. (남은 긴급 해제 0회)",
            viewModel.UsagePolicyStatusMessage);
    }

    [STATestMethod]
    public async Task ResetRestoresTheLatestSavedValuesInsteadOfProductDefaults()
    {
        var store = new RecordingStore(UsagePolicySettings.Default);
        using var runtime = CreateRuntime(
            store,
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        DailyUsageRestrictionViewModel monday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Monday);
        monday.IsRestrictionEnabled = true;
        monday.StartTime.Set(new TimeOnly(9, 0));
        monday.ReleaseTime.Set(new TimeOnly(10, 0));
        await viewModel.SaveWeeklyScheduleAsync();
        viewModel.EmergencyDurationMinutesText = "27";
        viewModel.EmergencySentenceCountText = "6";
        await viewModel.SaveEmergencyUnlockSettingsAsync();

        monday.IsRestrictionEnabled = false;
        monday.StartTime.HourText = "1";
        viewModel.EmergencyDurationMinutesText = "99";
        viewModel.EmergencySentenceCountText = "99";

        viewModel.ResetUsagePolicyEdits();

        Assert.IsTrue(monday.IsRestrictionEnabled);
        Assert.AreEqual("9", monday.StartTime.HourText);
        Assert.AreEqual("27", viewModel.EmergencyDurationMinutesText);
        Assert.AreEqual("6", viewModel.EmergencySentenceCountText);
    }

    private static MainWindowViewModel CreateViewModel(UsagePolicyRuntime runtime) =>
        CreateViewModel(runtime, lockReminderSettings: null, updateLockReminderSetting: null);

    private static MainWindowViewModel CreateViewModel(
        UsagePolicyRuntime runtime,
        LockReminderSettings? lockReminderSettings,
        Func<int, bool, Task>? updateLockReminderSetting) =>
        new(
            runtime,
            restartOnExitWhenUnlocked: true,
            updateRestartSetting: _ => Task.CompletedTask
#if DEBUG
            , requestLock: () => Task.CompletedTask,
            requestDevelopmentUnlock: () => Task.CompletedTask
#endif
            , lockReminderSettings: lockReminderSettings,
            updateLockReminderSetting: updateLockReminderSetting
            );

    private static System.Windows.Input.ICommand GetVoiceReminderCommand(
        MainWindowViewModel viewModel,
        int minutes) => minutes switch
        {
            30 => viewModel.ToggleVoiceReminder30MinutesCommand,
            10 => viewModel.ToggleVoiceReminder10MinutesCommand,
            5 => viewModel.ToggleVoiceReminder5MinutesCommand,
            1 => viewModel.ToggleVoiceReminder1MinuteCommand,
            _ => throw new ArgumentOutOfRangeException(nameof(minutes)),
        };

    private static bool GetVoiceReminderValue(
        MainWindowViewModel viewModel,
        int minutes) => minutes switch
        {
            30 => viewModel.VoiceReminder30Minutes,
            10 => viewModel.VoiceReminder10Minutes,
            5 => viewModel.VoiceReminder5Minutes,
            1 => viewModel.VoiceReminder1Minute,
            _ => throw new ArgumentOutOfRangeException(nameof(minutes)),
        };

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

    private static bool AllWeekdaysAreDisabled(WeeklyUsageRestrictionSchedule schedule)
    {
        for (int dayValue = 0; dayValue < 7; dayValue++)
        {
            if (schedule.GetRestriction((DayOfWeek)dayValue).IsEnabled)
            {
                return false;
            }
        }

        return true;
    }

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
            bool lockRequiredAfterRestart,
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
