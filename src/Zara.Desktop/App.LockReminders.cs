using System.Windows.Threading;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Infrastructure.Windows;

namespace Zara.Desktop;

public partial class App
{
    private LockReminderRuntime? _lockReminders;
    private readonly SemaphoreSlim _executionSettingsGate = new(1, 1);

    private LockReminderSettings CurrentLockReminderSettings => new(
        _restartSettings.VoiceReminder30Minutes,
        _restartSettings.VoiceReminder10Minutes,
        _restartSettings.VoiceReminder5Minutes,
        _restartSettings.VoiceReminder1Minute);

    private void InitializeLockReminders()
    {
        _lockReminders = new LockReminderRuntime(TimeProvider.System, new WpfLockReminderAudioPort());
        UpdateLockReminders();
    }

    private void UpdateLockReminders()
    {
        if (IsShuttingDown || _usagePolicyRuntime is null)
        {
            return;
        }

        if (_lockConditionRequired && !_usagePolicyRuntime.CurrentSnapshot.Evaluation.LockRequired)
        {
            _lockReminders?.Stop();
            return;
        }

        // Read the current snapshot when the dispatcher executes, not an older queued event.
        _lockReminders?.Observe(_usagePolicyRuntime.CurrentSnapshot, CurrentLockReminderSettings);
    }

    private void StopLockReminders()
    {
        LockReminderRuntime? reminders = _lockReminders;
        if (Dispatcher.CheckAccess())
        {
            reminders?.Stop();
        }
        else if (!Dispatcher.HasShutdownStarted)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => reminders?.Stop()));
        }
    }

    private async Task UpdateLockReminderSettingAsync(int minutes, bool enabled)
    {
        await _executionSettingsGate.WaitAsync().ConfigureAwait(true);
        try
        {
            ThrowIfShuttingDown();
            await GetUsagePolicyRuntime().RefreshAsync().ConfigureAwait(true);
            if (!GetUsagePolicyRuntime().CurrentSnapshot.Evaluation.IsSettingsChangeAllowed)
            {
                throw new UsagePolicySettingsLockedException();
            }

            LockReminderSettings settings = CurrentLockReminderSettings.WithEnabled(minutes, enabled);
            DesktopRestartSettings updated = _restartSettings with
            {
                VoiceReminder30Minutes = settings.ThirtyMinutes,
                VoiceReminder10Minutes = settings.TenMinutes,
                VoiceReminder5Minutes = settings.FiveMinutes,
                VoiceReminder1Minute = settings.OneMinute,
            };
            WindowsDesktopRestartSettingsStore store = _settingsStore ??
                throw new InvalidOperationException("The execution settings store is not initialized.");
            await store.SaveAsync(updated).ConfigureAwait(true);
            _restartSettings = updated;
            UpdateLockReminders();
        }
        finally
        {
            _executionSettingsGate.Release();
        }
    }
}
