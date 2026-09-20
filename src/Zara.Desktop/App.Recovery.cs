using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Zara.Application.Locking;
using Zara.Application.SystemPower;
using Zara.Desktop.Overlays;
using Zara.Infrastructure.Windows;

namespace Zara.Desktop;

public partial class App
{
    private readonly WindowsShutdownNotificationTracker _shutdownNotifications = new();
    private HwndSource? _shutdownNotificationWindow;
    private readonly ShutdownPresentationState _shutdownPresentation = new();
    private LockErrorDialog? _lockRecoveryDialog;
    private LockErrorDialog? _shutdownResultDialog;
    private long _notifiedRecoveryEpisode;
    private bool _sessionEnding;

    private void OnLockRecoveryStateChanged(object? sender, LockRecoveryState state)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        // The runtime can notify from its retry thread while holding its request gate.
        // Queue presentation instead of synchronously waiting for the UI thread.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateRecoveryPresentation));
    }

    private void UpdateRecoveryPresentation()
    {
        if (IsShuttingDown || _sessionEnding || _lockRuntime is null || _overlayPort is null)
        {
            return;
        }

        LockRecoveryState recovery = _lockRuntime.CurrentRecovery;
        _overlayPort.SetRecoveryActive(recovery.IsRecovering);
        _ = PublishCurrentProjectionBestEffortAsync();
        if (!recovery.IsRecovering)
        {
            _lockRecoveryDialog?.Close();
            return;
        }

        if (SystemShutdownRequestInProgress || _shutdownResultDialog is not null ||
            _lockRecoveryDialog is not null || _notifiedRecoveryEpisode == recovery.Episode)
        {
            return;
        }

        _notifiedRecoveryEpisode = recovery.Episode;
        var dialog = CreateLockErrorDialog(
            "잠금 오류",
            "일부 잠금 기능을 적용하지 못했습니다.\n가능한 잠금은 유지하며, 실패한 기능은 1분마다 복구를 다시 시도합니다.",
            allowRetry: false);
        _lockRecoveryDialog = dialog;
        try
        {
            _ = dialog.ShowDialog();
        }
        finally
        {
            _lockRecoveryDialog = null;
        }
    }

    private LockErrorDialog CreateLockErrorDialog(string title, string message, bool allowRetry)
    {
        var dialog = new LockErrorDialog(title, message, allowRetry);
        Window? owner = _overlayPort?.GetDialogOwner();
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog;
    }

    private void ShowShutdownResult(string message)
    {
        if (IsShuttingDown || _sessionEnding || _shutdownResultDialog is not null)
        {
            return;
        }

        _lockRecoveryDialog?.Close();
        var dialog = CreateLockErrorDialog("시스템 종료", message, allowRetry: true);
        _shutdownResultDialog = dialog;
        try
        {
            _ = dialog.ShowDialog();
        }
        finally
        {
            _shutdownResultDialog = null;
        }

        // Dispatch after the previous request and dialog have both finished. Repeated failures
        // open a new result without recursive modal calls or duplicate shutdown requests.
        if (dialog.RetryRequested && !IsShuttingDown && !_sessionEnding)
        {
            _ = Dispatcher.BeginInvoke(new Action(async () =>
                await RequestSystemShutdownAsync().ConfigureAwait(true)));
        }
        else
        {
            _ = Dispatcher.BeginInvoke(new Action(UpdateRecoveryPresentation));
        }
    }

    private void InitializeShutdownNotifications()
    {
        // A hidden top-level window receives session broadcasts and survives overlay replacement.
        // It shares the existing Dispatcher; message-only windows do not receive these broadcasts.
        var parameters = new HwndSourceParameters("ZARA shutdown notifications")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _shutdownNotificationWindow = new HwndSource(parameters);
        _shutdownNotificationWindow.AddHook(OnShutdownWindowMessage);
    }

    private nint OnShutdownWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        WindowsShutdownNotification? notification = _shutdownNotifications.ObserveMessage(message, wParam);
        if (notification is not null && _shutdownPresentation.ObserveTerminal(notification.RequestId))
        {
            _ = Dispatcher.BeginInvoke(new Action(async () =>
                await HandleShutdownNotificationAsync(notification).ConfigureAwait(true)));
        }

        if (message == 0x0011)
        {
            handled = true;
            return 1;
        }

        return 0;
    }

    private async Task HandleShutdownNotificationAsync(WindowsShutdownNotification notification)
    {
        if (notification.RequestId != _shutdownPresentation.ActiveRequestId || IsShuttingDown)
        {
            return;
        }

        CancelSystemShutdownWatchdog();
        if (notification.IsEnding)
        {
            _sessionEnding = true;
            _lockRuntime?.Dispose();
            return;
        }

        bool showResult = false;
        LockIntentSnapshot? resultIntent = null;
        try
        {
            SystemShutdownUseCase shutdown = _systemShutdown ??
                throw new InvalidOperationException("The shutdown use case is not initialized.");
            ShutdownCancellationRecoveryResult result = await shutdown
                .HandleShutdownCancellationAsync(notification.RequestId).ConfigureAwait(true);
            showResult = result is ShutdownCancellationRecoveryResult.LockRestored or
                ShutdownCancellationRecoveryResult.LockNotRequired or
                ShutdownCancellationRecoveryResult.RecoveryPending;
            resultIntent = GetLockRuntime().CurrentIntent;
        }
        catch (Exception exception)
        {
            Trace.TraceError("The canceled shutdown could not fully restore the lock: {0}", exception);
            // The runtime retains successful effects and retries the failed ones separately.
            showResult = _lockRuntime?.CurrentRecovery.IsRecovering == true;
            resultIntent = _lockRuntime?.CurrentIntent;
        }
        finally
        {
            CompleteShutdownAttempt(notification.RequestId);
            await PublishCurrentProjectionBestEffortAsync().ConfigureAwait(true);
        }

        if (showResult && resultIntent is not null && !IsShuttingDown &&
            _shutdownPresentation.CanShowResult(
                notification.RequestId, resultIntent, GetLockRuntime().CurrentIntent))
        {
            ShowShutdownResult("PC 종료가 취소되었습니다.");
        }
    }

    private void CompleteShutdownAttempt(Guid requestId)
    {
        _shutdownNotifications.CompleteRequest(requestId);
        if (!_shutdownPresentation.Complete(requestId))
        {
            return;
        }

        CancelSystemShutdownWatchdog();
        if (!IsShuttingDown)
        {
            _overlayPort?.SetSystemShutdownEnabled(isEnabled: true);
        }
    }

    private void DisposeRecoveryNotifications()
    {
        _shutdownNotifications.CompleteRequest(_shutdownPresentation.ActiveRequestId);
        _shutdownNotificationWindow?.RemoveHook(OnShutdownWindowMessage);
        _shutdownNotificationWindow?.Dispose();
        _shutdownNotificationWindow = null;
        _lockRecoveryDialog?.Close();
        _shutdownResultDialog?.Close();
    }
}
