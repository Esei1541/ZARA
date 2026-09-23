using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class MainWindowScheduleBindingTests
{
    [STATestMethod]
    public async Task HeaderAndEmergencyDaySelectorUseSavedAndDraftValuesSeparately()
    {
        using var runtime = CreateRuntime(UsagePolicySettings.Default.WithEmergencyUnlock(
            new EmergencyUnlockSettings(10, 3, true, DayOfWeek.Sunday, 3)));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        var window = CreateWindow(viewModel);
        try
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.MainTabs.SelectedIndex = 2;
            window.Show();
            window.ResetDayList.SelectedValue = DayOfWeek.Friday;
            DrainBindings(window);
            window.UpdateLayout();

            Assert.AreEqual(DayOfWeek.Friday, viewModel.EmergencyWeeklyResetDay);
            Assert.AreEqual("일요일 초기화", window.EmergencyResetText.Text);
            Assert.AreEqual("3회", window.EmergencyQuotaText.Text);
            AssertContained(window.LockCountdownText, window);
            AssertContained(window.EmergencyResetText, window);

            window.MainTabs.SelectedIndex = 0;
            window.MainTabs.SelectedIndex = 2;
            DrainBindings(window);
            Assert.AreEqual(DayOfWeek.Sunday, window.ResetDayList.SelectedValue);
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public void UsageCheckboxReflectsAndUpdatesTheWeekdayEditor()
    {
        var viewModel = new MainWindowViewModel(
            restartOnExitWhenUnlocked: true,
            saveExecutionSettings: (_, _) => Task.CompletedTask
#if DEBUG
            , requestLock: () => Task.CompletedTask,
            requestDevelopmentUnlock: () => Task.CompletedTask
#endif
            );
        DailyUsageRestrictionViewModel wednesday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Wednesday);
        wednesday.IsRestrictionEnabled = true;
        var window = new MainWindow(viewModel)
        {
            Left = -10_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Top = -10_000,
        };

        try
        {
            window.MainTabs.SelectedIndex = 1;
            viewModel.SelectedWeekday = wednesday;
            window.Show();
            window.UpdateLayout();
            CheckBox checkBox = window.WeeklyScheduleEditor.EnabledCheckBox;

            Assert.IsTrue(checkBox.IsChecked);
            Assert.AreEqual("활성화", checkBox.Content);

            checkBox.IsChecked = false;

            Assert.IsFalse(wednesday.IsRestrictionEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public async Task LeavingEachSettingsTabRestoresItsLatestSavedValues()
    {
        WeeklyUsageRestrictionSchedule schedule =
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(
                DayOfWeek.Monday,
                new DailyUsageRestriction(
                    isEnabled: true,
                    startTime: new TimeOnly(9, 0),
                    releaseTime: new TimeOnly(10, 0)));
        using var runtime = CreateRuntime(new UsagePolicySettings(
            schedule,
            new EmergencyUnlockSettings(durationMinutes: 17, sentenceCount: 4),
            Array.Empty<OutOfHoursReservation>()));
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        DailyUsageRestrictionViewModel monday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Monday);
        var window = CreateWindow(viewModel);

        try
        {
            window.Show();
            viewModel.RestartOnExitWhenUnlocked = false;
            viewModel.VoiceReminder30Minutes = false;
            window.MainTabs.SelectedIndex = 1;

            Assert.IsTrue(viewModel.RestartOnExitWhenUnlocked);
            Assert.IsTrue(viewModel.VoiceReminder30Minutes);
            monday.StartTime.HourText = "7";

            window.MainTabs.SelectedIndex = 2;

            Assert.AreEqual("09", monday.StartTime.HourText);
            viewModel.EmergencyDurationMinutesText = "99";

            window.MainTabs.SelectedIndex = 1;

            Assert.AreEqual("17", viewModel.EmergencyDurationMinutesText);
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public async Task ClosingTheWindowDiscardsTheCurrentTabEdit()
    {
        using var runtime = CreateRuntime(new UsagePolicySettings(
            WeeklyUsageRestrictionSchedule.Default,
            new EmergencyUnlockSettings(durationMinutes: 19, sentenceCount: 3),
            Array.Empty<OutOfHoursReservation>()));
        await runtime.InitializeAsync();
        var viewModel = CreateViewModel(runtime);
        var window = CreateWindow(viewModel);
        window.Show();
        window.MainTabs.SelectedIndex = 2;
        viewModel.EmergencyDurationMinutesText = "99";

        window.Close();

        Assert.AreEqual("19", viewModel.EmergencyDurationMinutesText);
    }

    [STATestMethod]
    public async Task ClosingTheWindowDiscardsBasicTabEdits()
    {
        using var runtime = CreateRuntime(UsagePolicySettings.Default);
        await runtime.InitializeAsync();
        var viewModel = CreateViewModel(runtime);
        var window = CreateWindow(viewModel);
        window.Show();
        viewModel.RestartOnExitWhenUnlocked = false;
        viewModel.VoiceReminder10Minutes = false;

        window.Close();

        Assert.IsTrue(viewModel.RestartOnExitWhenUnlocked);
        Assert.IsTrue(viewModel.VoiceReminder10Minutes);
    }

    [STATestMethod]
    public void AllReservationDeleteButtonsRemainEnabledWhenSettingsAreDisabled()
    {
        using var viewModel = new MainWindowViewModel(
            restartOnExitWhenUnlocked: true,
            saveExecutionSettings: (_, _) => Task.CompletedTask
#if DEBUG
            , requestLock: () => Task.CompletedTask,
            requestDevelopmentUnlock: () => Task.CompletedTask
#endif
            );
        var localNow = new DateTime(2026, 8, 10, 8, 30, 0);
        foreach (int dayOffset in new[] { -1, 0, 1 })
        {
            var reservation = new OutOfHoursReservation(
                Guid.NewGuid(), DateOnly.FromDateTime(localNow).AddDays(dayOffset),
                new TimeOnly(8, 0), new TimeOnly(9, 0), "삭제 가능");
            var row = new ReservationRowViewModel(reservation);
            row.UpdatePresentation(localNow);
            viewModel.Reservations.Add(row);
        }

        var window = CreateWindow(viewModel);
        try
        {
            window.MainTabs.SelectedIndex = 3;
            window.Show();
            var grid = FindVisualChild<DataGrid>(window.MainTabs);
            var addButton = FindVisualChild<Button>((System.Windows.DependencyObject)
                ((TabItem)window.MainTabs.SelectedItem).Content);
            Assert.IsFalse(addButton.IsEnabled);
            foreach (ReservationRowViewModel reservation in viewModel.Reservations)
            {
                grid.ScrollIntoView(reservation);
                grid.UpdateLayout();
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(reservation);
                var presenter = FindVisualChild<DataGridCellsPresenter>(row);
                var cell = (DataGridCell)presenter.ItemContainerGenerator.ContainerFromIndex(4);
                var deleteButton = FindVisualChild<Button>(cell);
                Assert.IsTrue(deleteButton.IsEnabled);
                Assert.AreEqual("예약 삭제", System.Windows.Automation.AutomationProperties.GetName(deleteButton));
                Assert.IsInstanceOfType<TextBlock>(deleteButton.Content);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public async Task DisplayedEveningInputsArePersistedWithoutMovingFocusOrCommittingARow()
    {
        var store = new InMemoryStore(UsagePolicySettings.Default);
        using var runtime = new UsagePolicyRuntime(store, new NoOpLockPort(), new EmptyPromptCatalog(),
            new FixedTimeProvider());
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        var editor = new WeeklyScheduleView { DataContext = viewModel };
        var window = new Window
        {
            Content = editor,
            Left = -10_000,
            Top = -10_000,
            Width = 900,
            Height = 600,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            editor.EnabledCheckBox.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            editor.HourInput.SetCurrentValue(TextBox.TextProperty, "19");
            editor.MinuteInput.SetCurrentValue(TextBox.TextProperty, "30");
            editor.ReleaseTimeButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            DrainBindings(window);
            editor.HourInput.SetCurrentValue(TextBox.TextProperty, "19");
            editor.MinuteInput.SetCurrentValue(TextBox.TextProperty, "31");

            Assert.AreEqual(new TimeOnly(19, 30), viewModel.SelectedWeekday.StartTime.ToTimeOnly());
            Assert.AreEqual(new TimeOnly(19, 31), viewModel.SelectedWeekday.ReleaseTime.ToTimeOnly());
            Assert.IsFalse(viewModel.WillLockImmediately);
            await viewModel.SaveWeeklyScheduleAsync();

            Assert.AreEqual(new TimeOnly(19, 30), store.Settings.WeeklySchedule.Wednesday.StartTime);
            Assert.AreEqual(new TimeOnly(19, 31), store.Settings.WeeklySchedule.Wednesday.ReleaseTime);
            Assert.IsFalse(runtime.CurrentSnapshot.Evaluation.LockRequired);
            Assert.IsFalse(viewModel.HasWeeklyScheduleChanges);
            Assert.AreEqual("19:30", viewModel.SelectedWeekday.StartTime.DisplayTime);
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public async Task SliderAndAdjustmentUpdateInputsAndInvalidInputCannotBeSaved()
    {
        var settings = UsagePolicySettings.Default.WithWeeklySchedule(
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(DayOfWeek.Wednesday,
                new DailyUsageRestriction(true, new TimeOnly(19, 30), new TimeOnly(21, 0))));
        using var runtime = new UsagePolicyRuntime(new InMemoryStore(settings), new NoOpLockPort(),
            new EmptyPromptCatalog(), new FixedTimeProvider());
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        var window = CreateWindow(viewModel);
        try
        {
            window.MainTabs.SelectedIndex = 1;
            window.Show();
            window.UpdateLayout();
            WeeklyScheduleView editor = window.WeeklyScheduleEditor;
            editor.TimeSlider.SetCurrentValue(RangeBase.ValueProperty, 1171d);
            DrainBindings(window);
            Assert.AreEqual("19", editor.HourInput.Text);
            Assert.AreEqual("31", editor.MinuteInput.Text);
            viewModel.SelectedWeekday.LaterHalfHourCommand.Execute(null);
            DrainBindings(window);
            Assert.AreEqual("20", editor.HourInput.Text);
            Assert.AreEqual("01", editor.MinuteInput.Text);
            Assert.AreEqual(1201d, editor.TimeSlider.Value);

            editor.HourInput.SetCurrentValue(TextBox.TextProperty, "24");
            DrainBindings(window);
            Assert.AreEqual("24", editor.HourInput.Text);
            Assert.AreEqual(1201d, editor.TimeSlider.Value);
            Assert.IsFalse(viewModel.SaveWeeklyScheduleCommand.CanExecute(null));
            Assert.Contains("수요일 시작 시각", viewModel.WeeklyScheduleValidationMessage);
            editor.HourInput.SetCurrentValue(TextBox.TextProperty, string.Empty);
            DrainBindings(window);
            Assert.AreEqual(string.Empty, editor.HourInput.Text);
            Assert.AreEqual(1201d, editor.TimeSlider.Value);
            viewModel.RevertWeekdayCommand.Execute(null);
            DrainBindings(window);
            Assert.AreEqual("19", editor.HourInput.Text);
            Assert.AreEqual("30", editor.MinuteInput.Text);
            Assert.AreEqual(1170d, editor.TimeSlider.Value);
            Assert.IsTrue(viewModel.SaveWeeklyScheduleCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    [DataRow(820d, 620d)]
    [DataRow(980d, 720d)]
    public async Task ScheduleInputsAndSaveActionsFitTheSupportedWindowSizes(double width, double height)
    {
        using var runtime = new UsagePolicyRuntime(new InMemoryStore(UsagePolicySettings.Default),
            new NoOpLockPort(), new EmptyPromptCatalog(), new FixedTimeProvider());
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        var window = CreateWindow(viewModel);
        window.Width = width;
        window.Height = height;
        try
        {
            window.MainTabs.SelectedIndex = 1;
            window.Show();
            DailyUsageRestrictionViewModel wednesday = viewModel.SelectedWeekday;
            wednesday.StartTime.Set(new TimeOnly(19, 0));
            wednesday.ReleaseTime.Set(new TimeOnly(19, 31));
            wednesday.IsRestrictionEnabled = true;
            await viewModel.SaveWeeklyScheduleAsync();
            DrainBindings(window);
            window.UpdateLayout();
            WeeklyScheduleView editor = window.WeeklyScheduleEditor;

            Assert.IsTrue(viewModel.IsWeeklyScheduleConfirmationVisible);
            Assert.IsTrue(editor.ConfirmSaveButton.IsVisible);
            Assert.AreEqual(FontWeights.Bold, editor.ImpactText.FontWeight);
            AssertContained(editor.SaveButton, editor);
            AssertContained(editor.ConfirmSaveButton, editor);

            editor.HourInput.BringIntoView();
            window.UpdateLayout();
            AssertContained(editor.HourInput, editor);
            AssertContained(editor.MinuteInput, editor);
            AssertContained(editor.TimeSlider, editor);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertContained(FrameworkElement element, FrameworkElement container)
    {
        Rect bounds = element.TransformToAncestor(container).TransformBounds(new Rect(element.RenderSize));
        Assert.IsTrue(element.ActualWidth > 0 && element.ActualHeight > 0);
        Assert.IsTrue(bounds.Left >= 0 && bounds.Top >= 0 &&
            bounds.Right <= container.ActualWidth && bounds.Bottom <= container.ActualHeight,
            $"{element.Name} bounds {bounds} exceed {container.RenderSize}.");
    }

    private static void DrainBindings(Window window) =>
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 23, 19, 26, 0, TimeSpan.Zero);
    }

    private static MainWindow CreateWindow(MainWindowViewModel viewModel) =>
        new(viewModel)
        {
            Left = -10_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Top = -10_000,
        };

    private static MainWindowViewModel CreateViewModel(UsagePolicyRuntime runtime) =>
        new(
            runtime,
            restartOnExitWhenUnlocked: true,
            saveExecutionSettings: (_, _) => Task.CompletedTask
#if DEBUG
            , requestLock: () => Task.CompletedTask,
            requestDevelopmentUnlock: () => Task.CompletedTask
#endif
            );

    private static UsagePolicyRuntime CreateRuntime(UsagePolicySettings settings) =>
        new(
            new InMemoryStore(settings),
            new NoOpLockPort(),
            new EmptyPromptCatalog(),
            TimeProvider.System);

    private sealed class InMemoryStore(UsagePolicySettings settings) : IUsagePolicySettingsStore
    {
        private UsagePolicySettings _settings = settings;
        internal UsagePolicySettings Settings => _settings;

        public Task<UsagePolicySettings> LoadAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task SaveAsync(
            UsagePolicySettings settings,
            CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpLockPort : IUsagePolicyLockPort
    {
        public Task ApplyPolicyLockRequirementAsync(
            bool lockRequired,
            bool lockRequiredAfterRestart,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyPromptCatalog : IEmergencyUnlockPromptCatalog
    {
        public Task<IReadOnlyList<string>> SelectDistinctAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private static T FindVisualChild<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            System.Windows.DependencyObject child =
                System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindVisualChildOrDefault<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        throw new InvalidOperationException($"{typeof(T).Name} was not found.");
    }

    private static T? FindVisualChildOrDefault<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            System.Windows.DependencyObject child =
                System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindVisualChildOrDefault<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
