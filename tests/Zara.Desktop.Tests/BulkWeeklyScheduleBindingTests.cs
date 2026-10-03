using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class BulkWeeklyScheduleBindingTests
{
    [STATestMethod]
    public async Task BulkEntryAndWeekdayListSwitchTheExistingTimeEditor()
    {
        using var runtime = CreateRuntime();
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        var editor = new WeeklyScheduleView { DataContext = viewModel };
        var window = CreateHost(editor);
        try
        {
            window.Show();
            DrainBindings(window);
            Assert.AreSame(viewModel.SelectedWeekday, editor.WeekdayList.SelectedItem);
            Assert.IsTrue(editor.EnabledCheckBox.IsVisible);
            Assert.IsTrue(Descendants<TextBlock>(editor.BulkScheduleButton).Any(text => text.Text == "한 번에 적용"));
            Assert.IsNotNull(editor.BulkScheduleButton.Command);

            editor.BulkScheduleButton.Command.Execute(null);
            DrainBindings(window);

            Assert.IsTrue(viewModel.IsBulkScheduleSelected);
            Assert.IsNull(editor.WeekdayList.SelectedItem);
            Assert.IsNull(viewModel.SelectedWeekdayListItem);
            Assert.AreSame(viewModel.BulkScheduleEditor, viewModel.ActiveScheduleTimeEditor);
            Assert.AreEqual("한 번에 적용", viewModel.ScheduleEditorTitle);
            Assert.IsFalse(editor.EnabledCheckBox.IsVisible);
            Assert.IsTrue(editor.ActivateBulkWeekdaysCheckBox.IsVisible);
            Assert.IsTrue(editor.ApplyBulkScheduleButton.IsVisible);
            ToggleButton[] targets = Descendants<ToggleButton>(editor.BulkWeekdayTargets).ToArray();
            Assert.HasCount(7, targets);
            Assert.HasCount(0, Descendants<CheckBox>(editor.BulkWeekdayTargets).ToArray());
            Assert.IsTrue(targets.All(target => target.IsChecked == false));
            Rect entryBounds = editor.BulkScheduleButton.TransformToAncestor(editor).TransformBounds(new Rect(editor.BulkScheduleButton.RenderSize));
            Rect listBounds = editor.WeekdayList.TransformToAncestor(editor).TransformBounds(new Rect(editor.WeekdayList.RenderSize));
            Assert.IsLessThanOrEqualTo(listBounds.Top, entryBounds.Bottom);

            DailyUsageRestrictionViewModel monday = viewModel.WeekdayRestrictions.Single(day => day.DayOfWeek == DayOfWeek.Monday);
            editor.WeekdayList.SelectedItem = monday;
            DrainBindings(window);

            Assert.IsFalse(viewModel.IsBulkScheduleSelected);
            Assert.AreSame(monday, viewModel.SelectedWeekday);
            Assert.AreSame(monday, viewModel.ActiveScheduleTimeEditor);
            Assert.IsTrue(editor.EnabledCheckBox.IsVisible);
            Assert.IsFalse(editor.BulkWeekdayTargets.IsVisible);
            Assert.IsFalse(editor.ApplyBulkScheduleButton.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public async Task TurningActivationOffKeepsTheTimeControlsAndTargetTogglesUsable()
    {
        using var runtime = CreateRuntime();
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        var editor = new WeeklyScheduleView { DataContext = viewModel };
        var window = CreateHost(editor);
        try
        {
            window.Show();
            viewModel.SelectBulkScheduleCommand.Execute(null);
            DrainBindings(window);
            editor.ActivateBulkWeekdaysCheckBox.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            DrainBindings(window);
            ToggleButton monday = Descendants<ToggleButton>(editor.BulkWeekdayTargets).Single(
                button => ((BulkWeekdaySelectionViewModel)button.DataContext).DayOfWeek == DayOfWeek.Monday);
            monday.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            DrainBindings(window);

            Assert.IsFalse(viewModel.BulkScheduleEditor.ActivateAfterApply);
            Assert.AreEqual(1, viewModel.BulkScheduleEditor.SelectedDayCount);
            Assert.AreEqual("1개 요일에 시간 반영", editor.ApplyBulkScheduleButton.Content);
            Assert.IsTrue(editor.StartTimeButton.IsEnabled);
            Assert.IsTrue(editor.ReleaseTimeButton.IsEnabled);
            Assert.IsTrue(editor.TimeSlider.IsEnabled);
            Assert.IsTrue(editor.HourInput.IsEnabled);
            Assert.IsTrue(editor.MinuteInput.IsEnabled);
            editor.HourInput.SetCurrentValue(TextBox.TextProperty, "23");
            editor.MinuteInput.SetCurrentValue(TextBox.TextProperty, "15");
            DrainBindings(window);
            Assert.AreEqual(new TimeOnly(23, 15), viewModel.BulkScheduleEditor.StartTime.ToTimeOnly());
            editor.ReleaseTimeButton.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            DrainBindings(window);
            editor.HourInput.SetCurrentValue(TextBox.TextProperty, "05");
            editor.MinuteInput.SetCurrentValue(TextBox.TextProperty, "30");
            DrainBindings(window);
            Assert.AreEqual(new TimeOnly(5, 30), viewModel.BulkScheduleEditor.ReleaseTime.ToTimeOnly());
            Assert.AreEqual(330d, editor.TimeSlider.Value);
            viewModel.BulkScheduleEditor.LaterHalfHourCommand.Execute(null);
            DrainBindings(window);
            Assert.AreEqual("06", editor.HourInput.Text);
            Assert.AreEqual("00", editor.MinuteInput.Text);
            Assert.IsTrue(editor.ApplyBulkScheduleButton.IsEnabled);
            viewModel.ApplyBulkScheduleCommand.Execute(null);
            DailyUsageRestrictionViewModel mondayEditor = viewModel.WeekdayRestrictions.Single(day => day.DayOfWeek == DayOfWeek.Monday);
            Assert.IsFalse(mondayEditor.IsRestrictionEnabled);
            Assert.AreEqual(new TimeOnly(23, 15), mondayEditor.StartTime.ToTimeOnly());
            Assert.AreEqual(new TimeOnly(6, 0), mondayEditor.ReleaseTime.ToTimeOnly());
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LeavingTheTabOrClosingTheWindowRestoresDraftsAndClearsGroupTargets(bool closeWindow)
    {
        using var runtime = CreateRuntime();
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        viewModel.SelectBulkScheduleCommand.Execute(null);
        viewModel.BulkScheduleEditor.Targets[0].IsSelected = true;
        viewModel.BulkScheduleEditor.StartTime.Set(new TimeOnly(23, 0));
        viewModel.BulkScheduleEditor.ReleaseTime.Set(new TimeOnly(5, 0));
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        var window = CreateWindow(viewModel);
        try
        {
            window.MainTabs.SelectedIndex = 1;
            window.Show();
            DailyUsageRestrictionViewModel day = viewModel.WeekdayRestrictions[0];
            day.StartTime.HourText = string.Empty;
            DrainBindings(window);

            if (closeWindow)
            {
                window.Close();
            }
            else
            {
                window.MainTabs.SelectedIndex = 0;
                window.MainTabs.SelectedIndex = 1;
                DrainBindings(window);
            }

            Assert.AreEqual(0, viewModel.BulkScheduleEditor.SelectedDayCount);
            Assert.AreEqual(runtime.CurrentSnapshot.Settings.WeeklySchedule.GetRestriction(day.DayOfWeek), day.ToRestriction());
            Assert.IsFalse(viewModel.HasWeeklyScheduleChanges);
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    [DataRow(820d, 620d)]
    [DataRow(980d, 720d)]
    public async Task BulkTargetsTimeInputsAndSaveConfirmationFitSupportedWindowSizes(double width, double height)
    {
        using var runtime = CreateRuntime();
        await runtime.InitializeAsync();
        using var viewModel = CreateViewModel(runtime);
        viewModel.SelectBulkScheduleCommand.Execute(null);
        viewModel.BulkScheduleEditor.SelectAllCommand.Execute(null);
        viewModel.BulkScheduleEditor.StartTime.Set(new TimeOnly(19, 0));
        viewModel.BulkScheduleEditor.ReleaseTime.Set(new TimeOnly(21, 0));
        viewModel.ApplyBulkScheduleCommand.Execute(null);
        var window = CreateWindow(viewModel);
        window.Width = width;
        window.Height = height;
        try
        {
            window.MainTabs.SelectedIndex = 1;
            window.Show();
            await viewModel.SaveWeeklyScheduleAsync();
            DrainBindings(window);
            WeeklyScheduleView editor = window.WeeklyScheduleEditor;
            ScrollViewer viewport = Descendants<ScrollViewer>(editor).First();
            Assert.IsTrue(viewModel.IsWeeklyScheduleConfirmationVisible);
            Assert.IsTrue(editor.ConfirmSaveButton.IsVisible);
            AssertContained(editor.SaveButton, editor);
            AssertContained(editor.ConfirmSaveButton, editor);
            editor.BulkScheduleButton.BringIntoView();
            DrainBindings(window);
            AssertContained(editor.BulkScheduleButton, viewport);
            editor.BulkWeekdayTargets.BringIntoView();
            DrainBindings(window);
            AssertContained(editor.BulkWeekdayTargets, viewport);
            foreach (ToggleButton target in Descendants<ToggleButton>(editor.BulkWeekdayTargets))
            {
                AssertContained(target, editor.BulkWeekdayTargets);
            }
            editor.HourInput.BringIntoView();
            DrainBindings(window);
            AssertContained(editor.HourInput, viewport);
            AssertContained(editor.MinuteInput, viewport);
            editor.TimeSlider.BringIntoView();
            DrainBindings(window);
            AssertContained(editor.TimeSlider, viewport);
            editor.ApplyBulkScheduleButton.BringIntoView();
            DrainBindings(window);
            AssertContained(editor.ApplyBulkScheduleButton, viewport);
            Assert.IsTrue(editor.ApplyBulkScheduleButton.IsEnabled);
            AssertContained(editor.SaveButton, editor);
            AssertContained(editor.ConfirmSaveButton, editor);
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public void BulkApplicationNotificationDisplaysApprovedMessageAndOneConfirmationAction()
    {
        const string message = "선택한 요일에 반영되었습니다.\n일정 저장을 누르면 잠금이 저장됩니다.";
        var dialog = new SettingsMessageDialog("한 번에 적용", message);
        Assert.AreEqual("한 번에 적용", dialog.Title);
        Assert.AreEqual(message, dialog.MessageText.Text);
        Assert.AreEqual(Visibility.Collapsed, dialog.DetailsPanel.Visibility);
        Assert.AreEqual(Visibility.Collapsed, dialog.CancelButton.Visibility);
        Assert.AreEqual("확인", dialog.AcceptButton.Content);
    }

    private static Window CreateHost(WeeklyScheduleView editor) => new()
    {
        Content = editor,
        Left = -10_000,
        Top = -10_000,
        Width = 900,
        Height = 600,
        ShowActivated = false,
        ShowInTaskbar = false,
    };

    private static MainWindow CreateWindow(MainWindowViewModel viewModel) => new(viewModel)
    {
        Left = -10_000,
        Top = -10_000,
        ShowActivated = false,
        ShowInTaskbar = false,
    };

    private static MainWindowViewModel CreateViewModel(UsagePolicyRuntime runtime) => new(
        runtime, restartOnExitWhenUnlocked: true, saveExecutionSettings: (_, _) => Task.CompletedTask
#if DEBUG
        , requestLock: () => Task.CompletedTask, requestDevelopmentUnlock: () => Task.CompletedTask
#endif
        );

    private static UsagePolicyRuntime CreateRuntime() =>
        new(new InMemoryStore(), new NoOpLockPort(), new EmptyPromptCatalog(), new FixedTimeProvider());

    private static void DrainBindings(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        window.UpdateLayout();
    }

    private static void AssertContained(FrameworkElement element, FrameworkElement container)
    {
        Rect bounds = element.TransformToAncestor(container).TransformBounds(new Rect(element.RenderSize));
        Assert.IsTrue(element.ActualWidth > 0 && element.ActualHeight > 0);
        Assert.IsTrue(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= container.ActualWidth && bounds.Bottom <= container.ActualHeight,
            $"{element.Name} bounds {bounds} exceed {container.RenderSize}.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 23, 19, 26, 0, TimeSpan.Zero);
    }

    private sealed class InMemoryStore : IUsagePolicySettingsStore
    {
        private UsagePolicySettings _settings = UsagePolicySettings.Default;
        public Task<UsagePolicySettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);
        public Task SaveAsync(UsagePolicySettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpLockPort : IUsagePolicyLockPort
    {
        public Task ApplyPolicyLockRequirementAsync(bool lockRequired, bool lockRequiredAfterRestart, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyPromptCatalog : IEmergencyUnlockPromptCatalog
    {
        public Task<IReadOnlyList<string>> SelectDistinctAsync(int count, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
