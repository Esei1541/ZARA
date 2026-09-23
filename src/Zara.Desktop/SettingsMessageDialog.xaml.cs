using System.Windows;

namespace Zara.Desktop;

internal partial class SettingsMessageDialog : Window
{
    internal SettingsMessageDialog(string title, string message, string? details = null, string? confirmText = null)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        DetailsText.Text = details ?? string.Empty;
        DetailsPanel.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = confirmText is null ? Visibility.Collapsed : Visibility.Visible;
        AcceptButton.Content = confirmText ?? "확인";
        AcceptButton.IsDefault = confirmText is null;
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
