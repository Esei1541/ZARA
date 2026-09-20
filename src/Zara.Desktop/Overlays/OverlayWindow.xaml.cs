using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace Zara.Desktop.Overlays;

/// <summary>
/// Displays one monitor's overlay and prevents an ordinary close from removing only that surface.
/// </summary>
internal sealed partial class OverlayWindow : Window
{
    private readonly Func<Task> _requestSystemShutdown;
    private readonly Func<Task> _requestEmergencyUnlock;
    private readonly Func<Task> _requestDevelopmentUnlock;
    private bool _coordinatorCloseAllowed;

    internal OverlayWindow(
        Func<Task> requestSystemShutdown,
        Func<Task> requestEmergencyUnlock,
        Func<Task> requestDevelopmentUnlock,
        bool showDevelopmentControls)
    {
        _requestSystemShutdown = requestSystemShutdown ??
            throw new ArgumentNullException(nameof(requestSystemShutdown));
        _requestEmergencyUnlock = requestEmergencyUnlock ??
            throw new ArgumentNullException(nameof(requestEmergencyUnlock));
        _requestDevelopmentUnlock = requestDevelopmentUnlock ??
            throw new ArgumentNullException(nameof(requestDevelopmentUnlock));
        InitializeComponent();
        DevelopmentUnlockButton.Visibility = showDevelopmentControls
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal void CloseFromCoordinator()
    {
        _coordinatorCloseAllowed = true;
        Close();
    }

    internal void SetSystemShutdownEnabled(bool isEnabled) =>
        SystemShutdownButton.IsEnabled = isEnabled;

    internal void SetEmergencyUnlockEnabled(bool isEnabled) =>
        EmergencyUnlockButton.IsEnabled = isEnabled;

    internal void SetRecoveryActive(bool isActive) =>
        RecoveryStatus.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_coordinatorCloseAllowed)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private async void SystemShutdown_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            "정말로 종료하시겠습니까?",
            "시스템 종료",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        SystemShutdownButton.IsEnabled = false;

        try
        {
            await _requestSystemShutdown().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The system shutdown request failed: {0}", exception);
            SystemShutdownButton.IsEnabled = true;
        }
    }

    private async void EmergencyUnlock_Click(object sender, RoutedEventArgs e)
    {
        EmergencyUnlockButton.IsEnabled = false;

        try
        {
            await _requestEmergencyUnlock().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The emergency unlock request failed: {0}", exception);
            EmergencyUnlockButton.IsEnabled = true;
        }
    }

    private async void DevelopmentUnlock_Click(object sender, RoutedEventArgs e)
    {
        DevelopmentUnlockButton.IsEnabled = false;

        try
        {
            await _requestDevelopmentUnlock().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The development unlock request failed: {0}", exception);
            DevelopmentUnlockButton.IsEnabled = true;
        }
    }
}
