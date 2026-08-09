using System.ComponentModel;
using System.Windows;

namespace Zara.Desktop.Overlays;

/// <summary>
/// Displays one monitor's overlay and prevents an ordinary close from removing only that surface.
/// </summary>
internal sealed partial class OverlayWindow : Window
{
    private readonly Func<Task> _requestDevelopmentUnlock;
    private bool _coordinatorCloseAllowed;

    internal OverlayWindow(Func<Task> requestDevelopmentUnlock)
    {
        _requestDevelopmentUnlock = requestDevelopmentUnlock ??
            throw new ArgumentNullException(nameof(requestDevelopmentUnlock));
        InitializeComponent();
    }

    internal void CloseFromCoordinator()
    {
        _coordinatorCloseAllowed = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_coordinatorCloseAllowed)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private async void DevelopmentUnlock_Click(object sender, RoutedEventArgs e)
    {
        DevelopmentUnlockButton.IsEnabled = false;
        UnlockErrorText.Visibility = Visibility.Collapsed;

        try
        {
            await _requestDevelopmentUnlock().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            UnlockErrorText.Text = $"즉시 해제에 실패했습니다. 다시 시도하십시오. {exception.Message}";
            UnlockErrorText.Visibility = Visibility.Visible;
            DevelopmentUnlockButton.IsEnabled = true;
        }
    }
}
