using System.Windows;
using Zara.Application.Updates;
using Zara.Desktop.Updates;
using Zara.Infrastructure.Windows.Updates;

namespace Zara.Desktop;

public partial class App
{
    private GitHubReleaseUpdateSource? _releaseUpdateSource;
    private ReleaseUpdateViewModel? _releaseUpdateViewModel;

    private void InitializeReleaseUpdates()
    {
        Version assemblyVersion = typeof(App).Assembly.GetName().Version ??
            throw new InvalidOperationException("The application version is unavailable.");
        var productVersion = new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
        _releaseUpdateSource = new GitHubReleaseUpdateSource();
        _releaseUpdateViewModel = new ReleaseUpdateViewModel(
            new ReleaseUpdateUseCase(productVersion, _releaseUpdateSource, new WindowsUpdateInstaller()),
            ConfirmReleaseUpdate,
            () => !IsShuttingDown && !_lockConditionRequired && !SystemShutdownRequestInProgress,
            () =>
            {
                ShowMainWindow();
                ((MainWindow)MainWindow).SelectUpdateTab();
            });
    }

    private bool ConfirmReleaseUpdate()
    {
        if (IsShuttingDown)
        {
            return false;
        }

        if (MainWindow is MainWindow { IsVisible: true } window)
        {
            return window.ConfirmReleaseUpdate();
        }

        var dialog = new SettingsMessageDialog("업데이트", ReleaseUpdateViewModel.UpdatePrompt, confirmText: "설치")
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
        };
        dialog.CancelButton.Content = "나중에";
        return dialog.ShowDialog() == true;
    }

    private void TryShowPendingUpdate()
    {
        if (_releaseUpdateViewModel is not null)
        {
            _ = _releaseUpdateViewModel.TryShowStartupPromptAsync();
        }
    }
}
