using System.ComponentModel;
using System.Windows;

namespace Zara.Desktop.Overlays;

/// <summary>
/// Displays one monitor's overlay and prevents an ordinary close from removing only that surface.
/// </summary>
internal sealed partial class OverlayWindow : Window
{
    private readonly Func<Task> _requestSystemShutdown;
    private readonly Func<Task> _requestDevelopmentUnlock;
    private bool _coordinatorCloseAllowed;

    internal OverlayWindow(
        Func<Task> requestSystemShutdown,
        Func<Task> requestDevelopmentUnlock,
        bool showDevelopmentControls)
    {
        _requestSystemShutdown = requestSystemShutdown ??
            throw new ArgumentNullException(nameof(requestSystemShutdown));
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

    internal void ShowOperationError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        OperationErrorText.Text = message;
        OperationErrorText.Visibility = Visibility.Visible;
    }

    internal void ClearOperationError()
    {
        OperationErrorText.Text = string.Empty;
        OperationErrorText.Visibility = Visibility.Collapsed;
    }

    internal void SetSystemShutdownEnabled(bool isEnabled) =>
        SystemShutdownButton.IsEnabled = isEnabled;

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
            "시스템을 종료하면 저장되지 않은 작업이 손실될 수 있습니다. 계속하시겠습니까?",
            "시스템 종료",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        ClearOperationError();
        SystemShutdownButton.IsEnabled = false;

        try
        {
            await _requestSystemShutdown().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowOperationError($"시스템 종료에 실패했습니다. 다시 시도하십시오. {exception.Message}");
            SystemShutdownButton.IsEnabled = true;
        }
    }

    private async void DevelopmentUnlock_Click(object sender, RoutedEventArgs e)
    {
        DevelopmentUnlockButton.IsEnabled = false;
        ClearOperationError();

        try
        {
            await _requestDevelopmentUnlock().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowOperationError(
                $"잠금 해제(개발용)에 실패했습니다. 다시 시도하십시오. {exception.Message}");
            DevelopmentUnlockButton.IsEnabled = true;
        }
    }
}
