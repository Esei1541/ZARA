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
