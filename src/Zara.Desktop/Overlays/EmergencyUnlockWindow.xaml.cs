using System.Windows;
using System.Windows.Input;
using Zara.Application.UsagePolicy;

namespace Zara.Desktop.Overlays;

/// <summary>
/// Shows one in-memory emergency challenge and forwards only the entered text to the application
/// use case. Clipboard and drop input are blocked at the WPF boundary.
/// </summary>
internal sealed partial class EmergencyUnlockWindow : Window
{
    private readonly Func<string, Task<bool>> _completeChallenge;

    internal EmergencyUnlockWindow(
        EmergencyUnlockChallenge challenge,
        Func<string, Task<bool>> completeChallenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        _completeChallenge = completeChallenge ??
            throw new ArgumentNullException(nameof(completeChallenge));
        InitializeComponent();
        DataContext = challenge;
        System.Windows.DataObject.AddPastingHandler(InputTextBox, OnPasting);
        Loaded += (_, _) => InputTextBox.Focus();
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e) =>
        e.CancelCommand();

    private void InputTextBox_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        bool pasteShortcut = (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            e.Key == Key.V;
        bool shiftInsert = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 &&
            e.Key == Key.Insert;
        if (pasteShortcut || shiftInsert)
        {
            e.Handled = true;
        }
    }

    private void InputTextBox_PreviewDrop(
        object sender,
        System.Windows.DragEventArgs e) =>
        e.Handled = true;

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        CompleteButton.IsEnabled = false;
        ValidationMessage.Text = string.Empty;

        try
        {
            bool completed = await _completeChallenge(InputTextBox.Text).ConfigureAwait(true);
            if (completed)
            {
                DialogResult = true;
                Close();
                return;
            }

            ValidationMessage.Text = "입력한 문장이 일치하지 않습니다.";
            InputTextBox.SelectAll();
            InputTextBox.Focus();
        }
        catch (Exception exception)
        {
            ValidationMessage.Text = exception.Message;
        }
        finally
        {
            if (IsVisible)
            {
                CompleteButton.IsEnabled = true;
            }
        }
    }
}
