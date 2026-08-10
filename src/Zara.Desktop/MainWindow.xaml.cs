using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _reservationPresentationTimer;

    internal MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        _reservationPresentationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _reservationPresentationTimer.Tick += OnReservationPresentationTimerTick;
        _reservationPresentationTimer.Start();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App { IsShuttingDown: false })
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _viewModel.RefreshReservationPresentation();
    }

    protected override void OnClosed(EventArgs e)
    {
        _reservationPresentationTimer.Stop();
        _reservationPresentationTimer.Tick -= OnReservationPresentationTimerTick;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnReservationPresentationTimerTick(object? sender, EventArgs e) =>
        _viewModel.RefreshReservationPresentation();

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
        catch (UsagePolicySettingsLockedException)
        {
            ShowMessage("사용 금지 시간에는 설정을 변경할 수 없습니다.", MessageBoxImage.Information);
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

            case ReservationChangeStatus.ActiveReservationCannotBeRemoved:
                ShowMessage("현재 적용 중인 예약은 삭제할 수 없습니다.", MessageBoxImage.Information);
                break;

            case ReservationChangeStatus.NotFound:
                ShowMessage("이미 삭제되었거나 찾을 수 없는 예약입니다.", MessageBoxImage.Information);
                break;

            default:
                ShowMessage("예약을 삭제하지 못했습니다.", MessageBoxImage.Information);
                break;
        }
    }

    private void ShowMessage(string message, MessageBoxImage image) =>
        System.Windows.MessageBox.Show(
            this,
            message,
            "시간 외 사용 예약",
            MessageBoxButton.OK,
            image);
}
