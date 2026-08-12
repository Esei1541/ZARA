using System.Windows;
using System.Windows.Input;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Zara.Desktop.Behaviors;

/// <summary>
/// Restricts a <see cref="WpfTextBox"/> to ASCII decimal digits for direct input and paste.
/// </summary>
public static class DigitsOnlyTextBoxBehavior
{
    /// <summary>Identifies whether the digits-only input behavior is active.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(DigitsOnlyTextBoxBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty OriginalImeValueProperty =
        DependencyProperty.RegisterAttached(
            "OriginalImeValue",
            typeof(bool?),
            typeof(DigitsOnlyTextBoxBehavior),
            new PropertyMetadata(null));

    /// <summary>Gets whether the digits-only input behavior is active for an element.</summary>
    /// <param name="element">The element whose attached value is read.</param>
    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    /// <summary>Activates or deactivates the digits-only input behavior for an element.</summary>
    /// <param name="element">The element whose attached value is changed.</param>
    /// <param name="value">Whether the behavior is active.</param>
    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    /// <summary>Returns whether text consists solely of one or more ASCII decimal digits.</summary>
    /// <param name="text">The direct-input or pasted text to inspect.</param>
    internal static bool IsAsciiDigits(string? text) =>
        !string.IsNullOrEmpty(text) && text.All(character => character is >= '0' and <= '9');

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not WpfTextBox textBox)
        {
            return;
        }

        if ((bool)eventArgs.OldValue)
        {
            textBox.PreviewTextInput -= OnPreviewTextInput;
            System.Windows.DataObject.RemovePastingHandler(textBox, OnPasting);
            RestoreImeSetting(textBox);
        }

        if ((bool)eventArgs.NewValue)
        {
            textBox.SetValue(
                OriginalImeValueProperty,
                textBox.ReadLocalValue(InputMethod.IsInputMethodEnabledProperty) is bool originalImeValue
                    ? originalImeValue
                    : null);
            InputMethod.SetIsInputMethodEnabled(textBox, false);
            textBox.PreviewTextInput += OnPreviewTextInput;
            System.Windows.DataObject.AddPastingHandler(textBox, OnPasting);
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs eventArgs) =>
        eventArgs.Handled = !IsAsciiDigits(eventArgs.Text);

    private static void OnPasting(object sender, DataObjectPastingEventArgs eventArgs)
    {
        string? pastedText = eventArgs.SourceDataObject.GetDataPresent(
            System.Windows.DataFormats.UnicodeText,
            autoConvert: true)
            ? eventArgs.SourceDataObject.GetData(
                System.Windows.DataFormats.UnicodeText,
                autoConvert: true) as string
            : null;

        if (!IsAsciiDigits(pastedText))
        {
            eventArgs.CancelCommand();
        }
    }

    private static void RestoreImeSetting(WpfTextBox textBox)
    {
        bool? originalValue = (bool?)textBox.GetValue(OriginalImeValueProperty);
        if (originalValue is null)
        {
            textBox.ClearValue(InputMethod.IsInputMethodEnabledProperty);
        }
        else
        {
            InputMethod.SetIsInputMethodEnabled(textBox, originalValue.Value);
        }

        textBox.ClearValue(OriginalImeValueProperty);
    }
}
