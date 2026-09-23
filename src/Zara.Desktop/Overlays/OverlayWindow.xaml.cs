using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
#if DEBUG
using Button = System.Windows.Controls.Button;
#endif

namespace Zara.Desktop.Overlays;

/// <summary>
/// Displays one monitor's overlay and prevents an ordinary close from removing only that surface.
/// </summary>
internal sealed partial class OverlayWindow : Window
{
    private static readonly SolidColorBrush RemainingNormalBrush = CreateRemainingNormalBrush();

    private static SolidColorBrush CreateRemainingNormalBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(209, 214, 222));
        brush.Freeze();
        return brush;
    }

    private readonly Func<Task> _requestSystemShutdown;
    private readonly Func<Task> _requestEmergencyUnlock;
#if DEBUG
    private readonly Func<Task> _requestDevelopmentUnlock;
    private readonly Button _developmentUnlockButton;
#endif

    private bool _coordinatorCloseAllowed;

    internal OverlayWindow(
        Func<Task> requestSystemShutdown,
        Func<Task> requestEmergencyUnlock
#if DEBUG
        , Func<Task> requestDevelopmentUnlock
#endif
        )
    {
        _requestSystemShutdown = requestSystemShutdown ??
            throw new ArgumentNullException(nameof(requestSystemShutdown));
        _requestEmergencyUnlock = requestEmergencyUnlock ??
            throw new ArgumentNullException(nameof(requestEmergencyUnlock));
#if DEBUG
        _requestDevelopmentUnlock = requestDevelopmentUnlock ??
            throw new ArgumentNullException(nameof(requestDevelopmentUnlock));
#endif

        InitializeComponent();
#if DEBUG
        _developmentUnlockButton = new Button
        {
            MinWidth = 168,
            Height = 42,
            Margin = new Thickness(4, 0, 4, 0),
            Padding = new Thickness(18, 0, 18, 0),
            Content = "잠금 해제(개발용)",
            FontWeight = FontWeights.SemiBold,
        };
        _developmentUnlockButton.Click += DevelopmentUnlock_Click;
        LockActions.Children.Add(_developmentUnlockButton);
#endif
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

    internal void SetEmergencyUnlockRemainingCount(int? remainingCount)
    {
        EmergencyUnlockRemainingText.Visibility = remainingCount is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (remainingCount is int count)
        {
            EmergencyUnlockRemainingText.Text = $"남은 긴급 해제 {count}회";
            EmergencyUnlockRemainingText.Foreground = count == 0
                ? Brushes.IndianRed
                : RemainingNormalBrush;
        }
    }

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
            _ = System.Windows.MessageBox.Show(
                this,
                exception is InvalidOperationException
                    ? exception.Message
                    : "긴급 해제를 시작하지 못했습니다. 다시 시도하세요.",
                "긴급 해제",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

#if DEBUG
    private async void DevelopmentUnlock_Click(object sender, RoutedEventArgs e)
    {
        _developmentUnlockButton.IsEnabled = false;

        try
        {
            await _requestDevelopmentUnlock().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The development unlock request failed: {0}", exception);
            _developmentUnlockButton.IsEnabled = true;
        }
    }
#endif
}
