using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Zara.Desktop.Behaviors;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class DigitsOnlyTextBoxBehaviorTests
{
    [TestMethod]
    [DataRow("0", true)]
    [DataRow("1234567890", true)]
    [DataRow("", false)]
    [DataRow("12a", false)]
    [DataRow("１２", false)]
    [DataRow("١٢", false)]
    [DataRow("1 2", false)]
    public void AcceptsOnlyAsciiDigits(string text, bool expected) =>
        Assert.AreEqual(expected, DigitsOnlyTextBoxBehavior.IsAsciiDigits(text));

    [STATestMethod]
    public void ActivationFiltersTextInputAndDeactivationRemovesTheHandler()
    {
        var textBox = new TextBox();
        DigitsOnlyTextBoxBehavior.SetIsEnabled(textBox, true);

        TextCompositionEventArgs digits = CreateTextInput(textBox, "12");
        textBox.RaiseEvent(digits);
        Assert.IsFalse(digits.Handled);

        TextCompositionEventArgs letters = CreateTextInput(textBox, "가");
        textBox.RaiseEvent(letters);
        Assert.IsTrue(letters.Handled);

        DigitsOnlyTextBoxBehavior.SetIsEnabled(textBox, false);
        TextCompositionEventArgs afterDeactivation = CreateTextInput(textBox, "가");
        textBox.RaiseEvent(afterDeactivation);
        Assert.IsFalse(afterDeactivation.Handled);
    }

    [STATestMethod]
    [DataRow("12", false)]
    [DataRow("12a", true)]
    public void PasteAllowsOnlyAsciiDigits(string pastedText, bool expectedCancellation)
    {
        var textBox = new TextBox();
        DigitsOnlyTextBoxBehavior.SetIsEnabled(textBox, true);
        var dataObject = new System.Windows.DataObject(
            System.Windows.DataFormats.UnicodeText,
            pastedText);
        var eventArgs = new DataObjectPastingEventArgs(
            dataObject,
            isDragDrop: false,
            System.Windows.DataFormats.UnicodeText)
        {
            RoutedEvent = System.Windows.DataObject.PastingEvent,
        };

        textBox.RaiseEvent(eventArgs);

        Assert.AreEqual(expectedCancellation, eventArgs.CommandCancelled);
    }

    [STATestMethod]
    public void DeactivationRestoresThePreviousImeSetting()
    {
        var textBox = new TextBox();
        InputMethod.SetIsInputMethodEnabled(textBox, true);

        DigitsOnlyTextBoxBehavior.SetIsEnabled(textBox, true);
        Assert.IsFalse(InputMethod.GetIsInputMethodEnabled(textBox));

        DigitsOnlyTextBoxBehavior.SetIsEnabled(textBox, false);
        Assert.IsTrue(InputMethod.GetIsInputMethodEnabled(textBox));
    }

    [STATestMethod]
    public void DeactivationClearsImeValueWhenTheTextBoxHadNoLocalSetting()
    {
        var textBox = new TextBox();
        Assert.AreSame(
            DependencyProperty.UnsetValue,
            textBox.ReadLocalValue(InputMethod.IsInputMethodEnabledProperty));

        DigitsOnlyTextBoxBehavior.SetIsEnabled(textBox, true);
        DigitsOnlyTextBoxBehavior.SetIsEnabled(textBox, false);

        Assert.AreSame(
            DependencyProperty.UnsetValue,
            textBox.ReadLocalValue(InputMethod.IsInputMethodEnabledProperty));
    }

    private static TextCompositionEventArgs CreateTextInput(TextBox textBox, string text)
    {
        var composition = new TextComposition(InputManager.Current, textBox, text);
        return new TextCompositionEventArgs(
            InputManager.Current.PrimaryKeyboardDevice,
            composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent,
        };
    }
}
