using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Zara.Desktop.ViewModels;
using Zara.Core.UsagePolicy;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class SettingsLayoutTests
{
    [STATestMethod]
    public void LockedWeekdayListKeepsItsTransparentBackground()
    {
        var window = CreateWindow();
        try
        {
            window.MainTabs.SelectedIndex = 1;
            window.Show();
            window.UpdateLayout();
            ListBox list = window.WeeklyScheduleEditor.WeekdayList;
            Assert.IsFalse(list.IsEnabled);
            Border chrome = Descendants<Border>(list).First();
            Assert.AreEqual(Colors.Transparent, ((SolidColorBrush)chrome.Background).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    [DataRow(820d)]
    [DataRow(980d)]
    [DataRow(1220d)]
    public void TabHeadersHaveRoomForTheirEntireText(double width)
    {
        var window = CreateWindow();
        window.Width = width;
        window.MainTabs.Items.Add(new TabItem { Header = "빌드" });
        try
        {
            window.Show();
            foreach (TabItem selected in window.MainTabs.Items)
            {
                window.MainTabs.SelectedItem = selected;
                window.UpdateLayout();
                double previousRight = 0;
                foreach (TabItem tab in window.MainTabs.Items)
                {
                    var presenter = (ContentPresenter)tab.Template.FindName("HeaderPresenter", tab);
                    var text = new FormattedText((string)tab.Header, CultureInfo.GetCultureInfo("ko-KR"),
                        FlowDirection.LeftToRight, new Typeface(tab.FontFamily, tab.FontStyle, TextElement.GetFontWeight(presenter), tab.FontStretch),
                        tab.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(tab).PixelsPerDip);
                    Rect bounds = tab.TransformToAncestor(window.MainTabs).TransformBounds(new Rect(tab.RenderSize));
                    double visibleWidth = VisualTreeHelper.GetClip(tab)?.Bounds.Width ?? tab.ActualWidth;
                    Assert.IsGreaterThanOrEqualTo(text.WidthIncludingTrailingWhitespace - 0.5, visibleWidth,
                        $"{tab.Header}: text {text.WidthIncludingTrailingWhitespace}, tab {tab.ActualWidth}, margin {tab.Margin}, font {tab.FontFamily} {tab.FontSize}.");
                    Assert.IsTrue(bounds.Left >= previousRight && bounds.Right <= window.MainTabs.ActualWidth);
                    previousRight = bounds.Right;
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    [DataRow(820d)]
    [DataRow(980d)]
    [DataRow(1220d)]
    public void SettingsKeepTheApprovedColumnsAndNormalBodyText(double width)
    {
        var window = CreateWindow();
        window.Width = width;
        try
        {
            window.Show();
            window.UpdateLayout();
            CheckBox[] options = [window.Voice30Option, window.Voice10Option, window.Voice5Option, window.Voice1Option];
            foreach (CheckBox option in options)
            {
                Assert.AreEqual(FontWeights.Normal, option.FontWeight);
                Assert.AreEqual(window.Voice30Option.ActualWidth, option.ActualWidth, 1d);
                Assert.IsGreaterThan(130d, option.ActualWidth);
            }

            window.MainTabs.SelectedIndex = 2;
            window.UpdateLayout();
            Point duration = window.DurationField.TranslatePoint(new Point(), window);
            Point sentence = window.SentenceField.TranslatePoint(new Point(), window);
            Assert.AreEqual(duration.Y, sentence.Y);
            Assert.AreEqual(duration.X + window.DurationField.ActualWidth + 30, sentence.X, 1d);
            Assert.AreEqual(window.MaximumField.TranslatePoint(new Point(), window).Y,
                window.ResetDayField.TranslatePoint(new Point(), window).Y);
            foreach (object day in window.ResetDayList.Items)
            {
                var item = (ListBoxItem)window.ResetDayList.ItemContainerGenerator.ContainerFromItem(day);
                TextBlock label = Descendants<TextBlock>(item).Single();
                Assert.HasCount(1, label.Text);
            }

            window.MainTabs.SelectedIndex = 3;
            window.UpdateLayout();
            Assert.AreEqual(window.ReservationGrid.ActualWidth * 0.22, window.ReservationDateColumn.ActualWidth, 1d);
            Assert.AreEqual(window.ReservationGrid.ActualWidth * 0.22, window.ReservationTimeColumn.ActualWidth, 1d);
            Assert.AreEqual(window.ReservationGrid.ActualWidth * 0.13, window.ReservationStateColumn.ActualWidth, 1d);
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    [STATestMethod]
    public void LongReservationListsScrollWithoutClippingTheLastRow()
    {
        var window = CreateWindow();
        var viewModel = (MainWindowViewModel)window.DataContext;
        for (int day = 1; day <= 30; day++)
        {
            viewModel.Reservations.Add(new ReservationRowViewModel(new OutOfHoursReservation(
                Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today).AddDays(day),
                new TimeOnly(0, 0), new TimeOnly(1, 0), string.Empty)));
        }

        try
        {
            window.Width = 820;
            window.Height = 620;
            window.MainTabs.SelectedIndex = 3;
            window.Show();
            window.UpdateLayout();
            DataGrid grid = window.ReservationGrid;
            Rect bounds = grid.TransformToAncestor(window).TransformBounds(new Rect(grid.RenderSize));
            Assert.IsLessThanOrEqualTo(window.ActualHeight, bounds.Bottom);
            ReservationRowViewModel last = viewModel.Reservations[^1];
            grid.ScrollIntoView(last);
            window.UpdateLayout();
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(last);
            Assert.IsNotNull(row);
            Rect rowBounds = row.TransformToAncestor(grid).TransformBounds(new Rect(row.RenderSize));
            Assert.IsTrue(rowBounds.Top >= 0 && rowBounds.Bottom <= grid.ActualHeight);
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public void TimeInputsDoNotApplyPaddingTwice()
    {
        var window = CreateWindow();
        try
        {
            window.MainTabs.SelectedIndex = 1;
            window.Show();
            window.UpdateLayout();
            TextBox input = window.WeeklyScheduleEditor.HourInput;
            Assert.IsTrue(input.ActualHeight is >= 36 and <= 42,
                $"Time input height {input.ActualHeight}; padding {input.Padding}; font {input.FontFamily} {input.FontSize}.");
        }
        finally
        {
            window.Close();
        }
    }

    private static MainWindow CreateWindow() => new(new MainWindowViewModel(
        restartOnExitWhenUnlocked: true,
        saveExecutionSettings: (_, _) => Task.CompletedTask
#if DEBUG
        , requestLock: () => Task.CompletedTask,
        requestDevelopmentUnlock: () => Task.CompletedTask
#endif
        ))
    {
        Left = -10_000,
        Top = -10_000,
        ShowActivated = false,
        ShowInTaskbar = false,
    };
}
