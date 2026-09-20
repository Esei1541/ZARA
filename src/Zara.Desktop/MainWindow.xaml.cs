using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;
#if DEBUG
using Button = System.Windows.Controls.Button;
#endif

namespace Zara.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    internal MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
#if DEBUG
        ShellActions.Children.Add(new Button
        {
            MinWidth = 156,
            Height = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Command = _viewModel.StartLockDemoCommand,
            Content = "오버레이 시연 시작",
            FontWeight = FontWeights.SemiBold,
        });
        ShellActions.Children.Add(new Button
        {
            MinWidth = 156,
            Height = 36,
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(16, 0, 16, 0),
            Command = _viewModel.DevelopmentUnlockCommand,
            Content = "잠금 해제(개발용)",
            FontWeight = FontWeights.SemiBold,
        });
#endif
        _viewModel.NotificationRequested += OnNotificationRequested;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.ResetUsagePolicyEdits();
        if (System.Windows.Application.Current is App { IsShuttingDown: false })
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.NotificationRequested -= OnNotificationRequested;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
        {
            return;
        }

        foreach (object removedItem in e.RemovedItems)
        {
            if (ReferenceEquals(removedItem, WeeklyScheduleTab))
            {
                _viewModel.ResetWeeklyScheduleEdits();
            }
            else if (ReferenceEquals(removedItem, EmergencyUnlockSettingsTab))
            {
                _viewModel.ResetEmergencyUnlockEdits();
            }
        }
    }

    private void OnNotificationRequested(
        object? sender,
        MainWindowNotificationEventArgs e) =>
        ShowMessage(
            e.Message,
            e.IsError ? MessageBoxImage.Error : MessageBoxImage.Information,
            e.Title);

    private async void AddReservation_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ReservationEditorDialog
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Draft is null)
        {
            return;
        }

        try
        {
            ReservationChangeStatus status = await _viewModel
                .AddReservationAsync(dialog.Draft)
                .ConfigureAwait(true);
            ShowReservationAddResult(status);
        }
        catch (UsagePolicySettingsLockedException)
        {
            ShowMessage("사용 금지 시간에는 설정을 변경할 수 없습니다.", MessageBoxImage.Information);
        }
        catch (ArgumentException exception)
        {
            ShowMessage(exception.Message, MessageBoxImage.Information);
        }
        catch (UsagePolicySettingsSavedButApplyFailedException)
        {
            ShowMessage(
                MainWindowViewModel.SavedButApplyFailedMessage,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            ShowMessage("예약을 등록하지 못했습니다. 잠시 후 다시 시도하세요.", MessageBoxImage.Error);
        }
    }

    private async void DeleteReservation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReservationRowViewModel reservation })
        {
            return;
        }

        try
        {
            ReservationChangeStatus status = await _viewModel
                .RemoveReservationAsync(reservation.Id)
                .ConfigureAwait(true);
            ShowReservationDeleteResult(status);
        }
        catch (UsagePolicySettingsSavedButApplyFailedException)
        {
            ShowMessage(
                MainWindowViewModel.SavedButApplyFailedMessage,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            ShowMessage("예약을 삭제하지 못했습니다. 잠시 후 다시 시도하세요.", MessageBoxImage.Error);
        }
    }

    private void ShowReservationAddResult(ReservationChangeStatus status)
    {
        switch (status)
        {
            case ReservationChangeStatus.Added:
                ShowMessage("시간 외 사용 예약을 등록했습니다.", MessageBoxImage.Information);
                break;

            case ReservationChangeStatus.ConflictsWithExisting:
                ShowMessage(
                    "해당 시각은 이미 등록되어 있습니다. 등록하려면 기존 예약을 삭제해주세요.",
                    MessageBoxImage.Information);
                break;

            default:
                ShowMessage("예약을 등록하지 못했습니다.", MessageBoxImage.Information);
                break;
        }
    }

    private void ShowReservationDeleteResult(ReservationChangeStatus status)
    {
        switch (status)
        {
            case ReservationChangeStatus.Removed:
                ShowMessage("시간 외 사용 예약을 삭제했습니다.", MessageBoxImage.Information);
                break;

            case ReservationChangeStatus.NotFound:
                ShowMessage("이미 삭제되었거나 찾을 수 없는 예약입니다.", MessageBoxImage.Information);
                break;

            default:
                ShowMessage("예약을 삭제하지 못했습니다.", MessageBoxImage.Information);
                break;
        }
    }

    private void ShowMessage(
        string message,
        MessageBoxImage image,
        string title = "시간 외 사용 예약") =>
        System.Windows.MessageBox.Show(
            this,
            message,
            title,
            MessageBoxButton.OK,
            image);
}
