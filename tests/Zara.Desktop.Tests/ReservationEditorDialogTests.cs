using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Zara.Desktop.Behaviors;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class ReservationEditorDialogTests
{
    private static readonly string[] ExpectedTimeInputNames =
    [
        "시작 시각 시간",
        "시작 시각 분",
        "종료 시각 시간",
        "종료 시각 분",
    ];

    [STATestMethod]
    public void EveryTimeTextBoxUsesTheDigitsOnlyBehavior()
    {
        var dialog = new ReservationEditorDialog();

        TextBox[] timeInputs = FindLogicalDescendants<TextBox>(dialog)
            .Where(textBox => ExpectedTimeInputNames.Contains(
                AutomationProperties.GetName(textBox),
                StringComparer.Ordinal))
            .ToArray();

        CollectionAssert.AreEquivalent(
            ExpectedTimeInputNames,
            timeInputs.Select(AutomationProperties.GetName).ToArray());
        Assert.IsTrue(timeInputs.All(DigitsOnlyTextBoxBehavior.GetIsEnabled));
    }

    [STATestMethod]
    public void ReservationFormUsesTwentyFourHourLabelsAndGuidance()
    {
        var dialog = new ReservationEditorDialog();
        string[] visibleTexts = FindLogicalDescendants<TextBlock>(dialog)
            .Select(textBlock => textBlock.Text)
            .ToArray();

        Assert.AreEqual("예약 추가", dialog.Title);
        CollectionAssert.Contains(visibleTexts, "시작 시각");
        CollectionAssert.Contains(visibleTexts, "종료 시각");
        CollectionAssert.Contains(
            visibleTexts,
            "시각은 24시간을 기준으로 입력해주세요. (예: 오후 07:30 → 19:30)");
        Assert.IsFalse(FindLogicalDescendants<ComboBox>(dialog).Any());

        TextBox[] hourInputs = FindLogicalDescendants<TextBox>(dialog)
            .Where(textBox => AutomationProperties.GetName(textBox) is "시작 시각 시간" or "종료 시각 시간")
            .ToArray();
        Assert.HasCount(2, hourInputs);
        Assert.IsTrue(hourInputs.All(textBox => AutomationProperties.GetHelpText(textBox) == "0부터 23까지 입력"));
    }

    private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject dependencyObject)
            {
                continue;
            }

            if (dependencyObject is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindLogicalDescendants<T>(dependencyObject))
            {
                yield return descendant;
            }
        }
    }
}
