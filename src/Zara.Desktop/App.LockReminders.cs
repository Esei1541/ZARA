using System.Windows.Threading;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Infrastructure.Windows;

namespace Zara.Desktop;

public partial class App
{
    private LockReminderRuntime? _lockReminders;
    private WindowsLockReminderLog? _lockReminderLog;
    private LockReminderDiagnostics? _lockReminderDiagnostics;
    private bool _remindersSuppressedForRecovery;
    private string? _lastReminderRefreshError;
    private readonly SemaphoreSlim _executionSettingsGate = new(1, 1);

    private LockReminderSettings CurrentLockReminderSettings => new(
        _restartSettings.VoiceReminder30Minutes,
        _restartSettings.VoiceReminder10Minutes,
        _restartSettings.VoiceReminder5Minutes,
        _restartSettings.VoiceReminder1Minute);

    private void InitializeLockReminders()
    {
        _lockReminderDiagnostics = new LockReminderDiagnostics();
#pragma warning disable CA1031 // Optional diagnostic storage must not prevent application startup.
        try
        {
            _lockReminderLog = new WindowsLockReminderLog();
            _lockReminderDiagnostics = new LockReminderDiagnostics(_lockReminderLog.Write);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("The lock reminder log could not initialize: {0}", exception);
        }
#pragma warning restore CA1031
        _lockReminderDiagnostics.Record($"session-start version={typeof(App).Assembly.GetName().Version} executable={Environment.ProcessPath} timezone={TimeZoneInfo.Local.Id}");
        _lockReminders = new LockReminderRuntime(TimeProvider.System,
            new WpfLockReminderAudioPort(_lockReminderDiagnostics), _lockReminderDiagnostics);
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
            if (!_remindersSuppressedForRecovery)
            {
                _lockReminderDiagnostics?.Record("suppressed reason=lock-recovery");
                _remindersSuppressedForRecovery = true;
            }
            _lockReminders?.Stop();
            return;
        }

        if (_remindersSuppressedForRecovery)
        {
            _lockReminderDiagnostics?.Record("resumed reason=lock-recovery-finished");
            _remindersSuppressedForRecovery = false;
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

    private async Task SaveExecutionSettingsAsync(bool restartOnExitWhenUnlocked, LockReminderSettings settings)
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

            DesktopRestartSettings updated = _restartSettings with
            {
                RestartOnExitWhenUnlocked = restartOnExitWhenUnlocked,
                VoiceReminder30Minutes = settings.ThirtyMinutes,
                VoiceReminder10Minutes = settings.TenMinutes,
                VoiceReminder5Minutes = settings.FiveMinutes,
                VoiceReminder1Minute = settings.OneMinute,
            };
            await SaveExecutionSettingsCoreAsync(updated).ConfigureAwait(true);
            _lockReminderDiagnostics?.Record($"settings-saved restart={restartOnExitWhenUnlocked} enabled30={settings.ThirtyMinutes} enabled10={settings.TenMinutes} enabled5={settings.FiveMinutes} enabled1={settings.OneMinute}");
            UpdateLockReminders();
        }
        finally
        {
            _executionSettingsGate.Release();
        }
    }
}
