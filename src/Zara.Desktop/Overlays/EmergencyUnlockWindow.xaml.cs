using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private readonly EmergencyUnlockInputMatcher _inputMatcher;

    internal EmergencyUnlockWindow(
        EmergencyUnlockChallenge challenge,
        Func<string, Task<bool>> completeChallenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        _completeChallenge = completeChallenge ??
            throw new ArgumentNullException(nameof(completeChallenge));
        _inputMatcher = new EmergencyUnlockInputMatcher(challenge);
        InitializeComponent();
        InputTextBox.TextChanged += InputTextBox_TextChanged;
        System.Windows.DataObject.AddPastingHandler(InputTextBox, OnPasting);
        RenderInputComparison(_inputMatcher.Compare(string.Empty));
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

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidationMessage.Text = string.Empty;
        RenderInputComparison(_inputMatcher.Compare(InputTextBox.Text));
    }

    private void RenderInputComparison(EmergencyUnlockInputComparison comparison)
    {
        ChallengeTextBlock.Inlines.Clear();
        foreach (EmergencyUnlockInputSegment segment in comparison.Segments)
        {
            AddSegment(segment);
        }
    }

    private void AddSegment(EmergencyUnlockInputSegment segment)
    {
        System.Windows.Media.Brush foreground = segment.State switch
        {
            EmergencyUnlockInputState.Matched =>
                (System.Windows.Media.Brush)FindResource("EmergencyUnlockMatchedTextBrush"),
            EmergencyUnlockInputState.Mismatched =>
                (System.Windows.Media.Brush)FindResource("EmergencyUnlockMismatchedTextBrush"),
            _ => ChallengeTextBlock.Foreground,
        };
        string[] lines = segment.Text.Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lineIndex > 0)
            {
                ChallengeTextBlock.Inlines.Add(new LineBreak());
            }

            if (lines[lineIndex].Length > 0)
            {
                ChallengeTextBlock.Inlines.Add(new Run(lines[lineIndex])
                {
                    Foreground = foreground,
                });
            }
        }
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        CompleteButton.IsEnabled = false;
        ValidationMessage.Text = string.Empty;
        string enteredText = InputTextBox.Text;

        try
        {
            bool completed = await _completeChallenge(enteredText).ConfigureAwait(true);
            if (completed)
            {
                DialogResult = true;
                Close();
                return;
            }

            EmergencyUnlockInputComparison comparison = _inputMatcher.Compare(enteredText);
            ValidationMessage.Text = GetValidationMessage(comparison);
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

    private static string GetValidationMessage(EmergencyUnlockInputComparison comparison)
    {
        if (comparison.IsExactMatch)
        {
            return "문장은 모두 일치하지만 긴급 해제를 시작하지 못했습니다. 다시 시도하세요.";
        }

        if (comparison.HasExcessInput)
        {
            return "예시문 뒤에 추가로 입력한 내용을 지우세요.";
        }

        return comparison.Segments.Any(
            segment => segment.State == EmergencyUnlockInputState.Mismatched)
            ? "빨간색으로 표시된 부분을 확인하세요."
            : "예시문을 끝까지 입력하세요.";
    }
}
