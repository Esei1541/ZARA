using System.Windows;

namespace Zara.Desktop.Overlays;

internal sealed partial class LockErrorDialog : Window
{
    private readonly bool _allowRetry;

    internal LockErrorDialog(string title, string message, bool allowRetry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        _allowRetry = allowRetry;
        RetryButton.Visibility = allowRetry ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = allowRetry;
    }

    internal bool RetryRequested { get; private set; }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowRetry)
        {
            return;
        }

        RetryRequested = true;
        Close();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => Close();
}
