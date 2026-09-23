using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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

    [STATestMethod]
    public void ReservationDialogUsesSingleCompactPanelAndKeepsInputsInsideCards()
    {
        var dialog = new ReservationEditorDialog
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
        };

        try
        {
            dialog.Show();
            dialog.UpdateLayout();

            Assert.AreEqual(WindowStyle.None, dialog.WindowStyle);
            Assert.IsTrue(dialog.AllowsTransparency);
            Assert.AreEqual(510d, dialog.ActualWidth, 0.5d);
            Assert.AreEqual(12d, dialog.DialogPanel.CornerRadius.TopLeft);
            Assert.AreEqual(new Thickness(24), dialog.DialogPanel.Padding);
            Assert.AreEqual(dialog.ReservationDatePicker.ActualWidth,
                dialog.StartTimeCard.ActualWidth + 16 + dialog.EndTimeCard.ActualWidth, 0.5d);
            Assert.IsGreaterThan(150d, dialog.StartTimeCard.ActualWidth);
            Assert.IsGreaterThan(150d, dialog.EndTimeCard.ActualWidth);

            TextBox[] timeInputs = FindLogicalDescendants<TextBox>(dialog)
                .Where(textBox => ExpectedTimeInputNames.Contains(
                    AutomationProperties.GetName(textBox), StringComparer.Ordinal))
                .ToArray();
            Assert.IsTrue(timeInputs.All(textBox => Math.Abs(textBox.ActualWidth - 55d) < 0.5d));
            Assert.IsTrue(timeInputs.All(textBox => textBox.Padding == new Thickness(5)));
            foreach (TextBox input in timeInputs)
            {
                Border card = AutomationProperties.GetName(input).StartsWith("시작", StringComparison.Ordinal)
                    ? dialog.StartTimeCard : dialog.EndTimeCard;
                Rect bounds = input.TransformToAncestor(card).TransformBounds(new Rect(input.RenderSize));
                Assert.IsTrue(bounds.Left >= 14 && bounds.Right <= card.ActualWidth - 14);
                Assert.IsTrue(input.ActualHeight is >= 36 and <= 44);
            }

            Button[] cancelButtons = FindLogicalDescendants<Button>(dialog)
                .Where(button => button.IsCancel)
                .ToArray();
            Assert.HasCount(2, cancelButtons);
            Assert.IsTrue(cancelButtons.Any(button => AutomationProperties.GetName(button) == "닫기"));
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    public void CalendarSelectionUpdatesTheReservationDate()
    {
        var dialog = new ReservationEditorDialog { Left = -10_000, Top = -10_000, ShowActivated = false };
        try
        {
            dialog.Show();
            dialog.UpdateLayout();
            DatePicker picker = dialog.ReservationDatePicker;
            var calendarButton = (Button)picker.Template.FindName("PART_Button", picker);
            calendarButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
            Assert.IsTrue(popup.IsOpen);
            var calendar = (Calendar)popup.Child;
            DateTime selectedDate = DateTime.Today.AddDays(3);
            calendar.SelectedDate = selectedDate;
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.AreEqual(selectedDate, picker.SelectedDate);
            var viewModel = (ViewModels.ReservationEditorViewModel)dialog.DataContext;
            Assert.AreEqual(selectedDate, viewModel.SelectedDate);
            picker.IsDropDownOpen = false;
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    public void InvalidReservationTimeShowsInlineValidation()
    {
        var dialog = new ReservationEditorDialog { Left = -10_000, Top = -10_000, ShowActivated = false };
        try
        {
            dialog.Show();
            TextBox startHour = FindLogicalDescendants<TextBox>(dialog)
                .Single(textBox => AutomationProperties.GetName(textBox) == "시작 시각 시간");
            startHour.SetCurrentValue(TextBox.TextProperty, "99");
            dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.IsNull(dialog.Draft);
            Assert.AreEqual(Visibility.Visible, dialog.ValidationMessage.Visibility);
            StringAssert.Contains(dialog.ValidationMessage.Text, "시작 시각");
        }
        finally
        {
            dialog.Close();
        }
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
