using System.ComponentModel;
using System.Windows;

namespace Zara.Desktop;

/// <summary>Shows manual startup progress, consent and one recoverable failure at a time.</summary>
internal partial class StartupWindow : Window
{
    private readonly CancellationTokenSource _cancellation;
    private TaskCompletionSource<bool>? _choice;
    private bool _completed;

    internal StartupWindow(CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
        InitializeComponent();
    }

    internal void ShowProgress()
    {
        MessageText.Text = "ZARA 서비스를 시작하고 앱을 준비하고 있습니다...";
        DetailsPanel.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Collapsed;
    }

    internal Task<bool> ConfirmElevationAsync(CancellationToken cancellationToken) =>
        AskAsync("ZARA 서비스를 시작하려면 관리자 권한이 필요합니다. 계속하면 Windows에서 권한을 요청합니다.",
            "서비스 시작", null, cancellationToken);

    internal Task<bool> OfferRetryAsync(StartupFailurePresentation failure, CancellationToken cancellationToken) =>
        AskAsync(failure.Message, failure.CanRetry ? "다시 시도" : null, failure.Details, cancellationToken);

    private async Task<bool> AskAsync(string message, string? action, string? details, CancellationToken cancellationToken)
    {
        var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _choice = choice;
        MessageText.Text = message;
        DetailsText.Text = details ?? string.Empty;
        DetailsPanel.Visibility = details is null ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = action;
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        using CancellationTokenRegistration registration = cancellationToken.Register(() => choice.TrySetCanceled(cancellationToken));
        try
        {
            return await choice.Task.ConfigureAwait(true);
        }
        finally
        {
            _choice = null;
        }
    }

    internal void Complete()
    {
        _completed = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_completed)
        {
            _choice?.TrySetResult(false);
            _cancellation.Cancel();
        }
        base.OnClosing(e);
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        ActionButton.IsEnabled = false;
        _choice?.TrySetResult(true);
        ShowProgress();
        ActionButton.IsEnabled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
