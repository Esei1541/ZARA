using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class MainWindowScheduleBindingTests
{
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
            window.Show();
            window.WeekdayRestrictionsGrid.BringIntoView();
            window.WeekdayRestrictionsGrid.ScrollIntoView(wednesday);
            window.WeekdayRestrictionsGrid.UpdateLayout();
            var column = (DataGridTemplateColumn)window.WeekdayRestrictionsGrid.Columns[1];
            var row = (DataGridRow)window.WeekdayRestrictionsGrid.ItemContainerGenerator
                .ContainerFromItem(wednesday);
            var presenter = FindVisualChild<DataGridCellsPresenter>(row);
            var cell = (DataGridCell)presenter.ItemContainerGenerator.ContainerFromIndex(1);
            var checkBox = FindVisualChild<CheckBox>(cell);

            Assert.IsTrue(checkBox.IsChecked);

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

            Assert.AreEqual("9", monday.StartTime.HourText);
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
                var cell = (DataGridCell)presenter.ItemContainerGenerator.ContainerFromIndex(5);
                var deleteButton = FindVisualChild<Button>(cell);
                Assert.IsTrue(deleteButton.IsEnabled);
                Assert.AreEqual("삭제", deleteButton.Content);
            }
        }
        finally
        {
            window.Close();
        }
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
