using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class LockReminderSettingsBindingTests
{
    private static readonly string[] ReminderSectionText =
        ["음성 안내", "잠금 시각이 다가오면 음성 안내를 출력합니다."];
    private static readonly (string Content, string Value, string Command)[] ExpectedBindings =
    [
        ("30분 전", "VoiceReminder30Minutes", "ToggleVoiceReminder30MinutesCommand"),
        ("10분 전", "VoiceReminder10Minutes", "ToggleVoiceReminder10MinutesCommand"),
        ("5분 전", "VoiceReminder5Minutes", "ToggleVoiceReminder5MinutesCommand"),
        ("1분 전", "VoiceReminder1Minute", "ToggleVoiceReminder1MinuteCommand"),
    ];

    [STATestMethod]
    public void MainWindowBindsEveryReminderCheckboxOneWayToItsCommandAndSettingsGate()
    {
        using var viewModel = new MainWindowViewModel(
            restartOnExitWhenUnlocked: true,
            updateRestartSetting: _ => Task.CompletedTask
#if DEBUG
            , requestLock: () => Task.CompletedTask,
            requestDevelopmentUnlock: () => Task.CompletedTask
#endif
            , lockReminderSettings: new LockReminderSettings(
                ThirtyMinutes: false,
                TenMinutes: true,
                FiveMinutes: false,
                OneMinute: true),
            updateLockReminderSetting: (_, _) => Task.CompletedTask
            );
        var window = CreateWindow(viewModel);

        try
        {
            window.Show();
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.UpdateLayout();
            Dictionary<string, CheckBox> reminderCheckBoxes = GetReminderCheckBoxes(window);

            Assert.HasCount(4, reminderCheckBoxes);
            foreach ((string content, string value, string command) in ExpectedBindings)
            {
                CheckBox checkBox = reminderCheckBoxes[content];
                Binding valueBinding = BindingOperations.GetBinding(
                    checkBox,
                    ToggleButton.IsCheckedProperty)!;
                Binding commandBinding = BindingOperations.GetBinding(
                    checkBox,
                    ButtonBase.CommandProperty)!;
                Binding enabledBinding = BindingOperations.GetBinding(
                    checkBox,
                    UIElement.IsEnabledProperty)!;

                Assert.AreEqual(value, valueBinding.Path.Path);
                Assert.AreEqual(BindingMode.OneWay, valueBinding.Mode);
                Assert.AreEqual(command, commandBinding.Path.Path);
                Assert.AreEqual("CanChangeSettings", enabledBinding.Path.Path);
            }

            Assert.IsFalse(reminderCheckBoxes["30분 전"].IsChecked);
            Assert.IsTrue(reminderCheckBoxes["10분 전"].IsChecked);
            Assert.IsFalse(reminderCheckBoxes["5분 전"].IsChecked);
            Assert.IsTrue(reminderCheckBoxes["1분 전"].IsChecked);
            var reminderRow = (StackPanel)reminderCheckBoxes["30분 전"].Parent;
            Assert.AreEqual(Orientation.Horizontal, reminderRow.Orientation);
            CollectionAssert.AreEqual(
                ExpectedBindings.Select(binding => binding.Content).ToArray(),
                reminderRow.Children.OfType<CheckBox>().Select(checkBox => (string)checkBox.Content).ToArray());
            var reminderSection = (StackPanel)reminderRow.Parent;
            CollectionAssert.AreEqual(
                ReminderSectionText,
                reminderSection.Children.OfType<TextBlock>().Select(text => text.Text).ToArray());
            foreach (CheckBox checkBox in reminderCheckBoxes.Values)
            {
                Point location = checkBox.TranslatePoint(new Point(), window);
                Assert.IsLessThan(window.ActualWidth, location.X + checkBox.ActualWidth);
                Assert.IsLessThan(window.ActualHeight, location.Y + checkBox.ActualHeight);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public async Task ReminderCheckboxesAreDisabledDuringUsageBan()
    {
        WeeklyUsageRestrictionSchedule schedule =
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(
                DayOfWeek.Monday,
                new DailyUsageRestriction(
                    isEnabled: true,
                    startTime: new TimeOnly(7, 0),
                    releaseTime: new TimeOnly(10, 0)));
        using var runtime = new UsagePolicyRuntime(
            new InMemoryStore(new UsagePolicySettings(
                schedule,
                EmergencyUnlockSettings.Default,
                Array.Empty<OutOfHoursReservation>())),
            new NoOpLockPort(),
            new EmptyPromptCatalog(),
            new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero)));
        await runtime.InitializeAsync();
        using var viewModel = new MainWindowViewModel(
            runtime,
            restartOnExitWhenUnlocked: true,
            updateRestartSetting: _ => Task.CompletedTask
#if DEBUG
            , requestLock: () => Task.CompletedTask,
            requestDevelopmentUnlock: () => Task.CompletedTask
#endif
            , lockReminderSettings: LockReminderSettings.Default,
            updateLockReminderSetting: (_, _) => Task.CompletedTask
            );
        var window = CreateWindow(viewModel);

        try
        {
            window.Show();
            Dictionary<string, CheckBox> reminderCheckBoxes = GetReminderCheckBoxes(window);

            Assert.IsTrue(reminderCheckBoxes.Values.All(checkBox => !checkBox.IsEnabled));
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

    private static Dictionary<string, CheckBox> GetReminderCheckBoxes(DependencyObject parent) =>
        FindVisualDescendants<CheckBox>(parent)
            .Where(checkBox => ExpectedBindings.Any(binding => Equals(binding.Content, checkBox.Content)))
            .ToDictionary(checkBox => (string)checkBox.Content);

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

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

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
