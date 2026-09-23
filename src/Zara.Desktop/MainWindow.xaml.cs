using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
#if LOCAL_BUILD_UPDATES
using Microsoft.Win32;
using Zara.Application.LocalBuilds;
using Zara.Desktop.LocalBuilds;
#endif
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;
using Zara.Desktop.Updates;
#if DEBUG
using Button = System.Windows.Controls.Button;
#endif

namespace Zara.Desktop;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The window disposes its local build view model in OnClosed; App owns the shared release update view model.")]
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ReleaseUpdateViewModel? _releaseUpdateViewModel;
    private readonly TabItem? _releaseUpdateTab;
#if LOCAL_BUILD_UPDATES
    private readonly LocalBuildViewModel? _localBuildViewModel;
    private readonly TabItem? _localBuildTab;
#endif

    internal MainWindow(
        MainWindowViewModel viewModel
#if LOCAL_BUILD_UPDATES
        , ILocalBuildUpdates? localBuildUpdates = null
#endif
        , ReleaseUpdateViewModel? releaseUpdates = null
        )
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
#if LOCAL_BUILD_UPDATES
        if (localBuildUpdates is not null)
        {
            _localBuildViewModel = new LocalBuildViewModel(
                localBuildUpdates,
                SelectLocalBuildDirectory,
                ConfirmLocalBuildInstall);
            _localBuildTab = new TabItem
            {
                Header = "빌드",
                Content = new LocalBuildView(_localBuildViewModel),
            };
            MainTabs.Items.Add(_localBuildTab);
        }
#endif
        _releaseUpdateViewModel = releaseUpdates;
        if (releaseUpdates is not null)
        {
            _releaseUpdateTab = new TabItem
            {
                Header = "업데이트",
                Content = new ReleaseUpdateView(releaseUpdates),
            };
            MainTabs.Items.Add(_releaseUpdateTab);
        }
#if DEBUG
        ShellActions.Children.Add(new Button
        {
            MinWidth = 156,
            Height = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Command = _viewModel.StartLockDemoCommand,
            Content = "오버레이 시연 시작",
            Style = (Style)FindResource("Zara.Button"),
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
            Style = (Style)FindResource("Zara.Button"),
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
#if LOCAL_BUILD_UPDATES
        _localBuildViewModel?.Dispose();
#endif
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
        {
            return;
        }

        foreach (object removedItem in e.RemovedItems)
        {
            if (ReferenceEquals(removedItem, BasicSettingsTab))
            {
                _viewModel.ResetExecutionSettingsEdits();
            }
            else if (ReferenceEquals(removedItem, WeeklyScheduleTab))
            {
                _viewModel.ResetWeeklyScheduleEdits();
            }
            else if (ReferenceEquals(removedItem, EmergencyUnlockSettingsTab))
            {
                _viewModel.ResetEmergencyUnlockEdits();
            }
        }

        foreach (object addedItem in e.AddedItems)
        {
            if (ReferenceEquals(addedItem, _releaseUpdateTab) && _releaseUpdateViewModel is not null)
            {
                await _releaseUpdateViewModel.RefreshAsync().ConfigureAwait(true);
            }
#if LOCAL_BUILD_UPDATES
            if (ReferenceEquals(addedItem, _localBuildTab) && _localBuildViewModel is not null)
            {
                await _localBuildViewModel.EnterAsync().ConfigureAwait(true);
            }
#endif
        }
    }

    internal void SelectUpdateTab() => MainTabs.SelectedItem = _releaseUpdateTab;

    internal bool ConfirmReleaseUpdate()
    {
        var dialog = new SettingsMessageDialog("업데이트", ReleaseUpdateViewModel.UpdatePrompt, confirmText: "설치");
        dialog.CancelButton.Content = "나중에";
        return ShowSettingsDialog(dialog) == true;
    }

#if LOCAL_BUILD_UPDATES
    private string? SelectLocalBuildDirectory(string currentDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "로컬 빌드 폴더 선택",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            dialog.InitialDirectory = currentDirectory;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private bool ConfirmLocalBuildInstall(LocalBuildItemViewModel build)
    {
        return ShowSettingsDialog(new SettingsMessageDialog(
            "선택 빌드로 업데이트",
            "ZARA를 종료하고 선택한 빌드의 설치를 시작합니다.",
            $"{build.VersionName} · {build.Configuration}\n{build.Branch}\n{build.CreatedAtText}",
            "업데이트")) == true;
    }
#endif

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
        if (ShowSettingsDialog(dialog) != true || dialog.Draft is null)
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

        string details = $"{reservation.Date:yyyy.MM.dd} · {reservation.TimeRange}";
        if (!string.IsNullOrEmpty(reservation.Memo))
        {
            details += $"\n{reservation.Memo}";
        }

        if (ShowSettingsDialog(new SettingsMessageDialog(
            "예약 삭제", "선택한 예약을 삭제하시겠습니까?", details, "삭제")) != true)
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

    private void ReservationGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ReservationDateColumn.Width = new DataGridLength(e.NewSize.Width * 0.22);
        ReservationTimeColumn.Width = new DataGridLength(e.NewSize.Width * 0.22);
        ReservationStateColumn.Width = new DataGridLength(e.NewSize.Width * 0.13);
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
        string title = "시간 외 사용 예약")
    {
        var dialog = new SettingsMessageDialog(title, message);
        if (image == MessageBoxImage.Error)
        {
            dialog.MessageText.Foreground = (System.Windows.Media.Brush)FindResource("Zara.Brush.Danger");
        }

        ShowSettingsDialog(dialog);
    }

    private bool? ShowSettingsDialog(Window dialog)
    {
        dialog.Owner = this;
        DialogDimmer.Visibility = Visibility.Visible;
        try
        {
            return dialog.ShowDialog();
        }
        finally
        {
            DialogDimmer.Visibility = Visibility.Collapsed;
        }
    }
}
