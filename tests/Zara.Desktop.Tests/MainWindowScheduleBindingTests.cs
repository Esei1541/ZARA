using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class MainWindowScheduleBindingTests
{
    [STATestMethod]
    public void UsageCheckboxReflectsAndUpdatesTheWeekdayEditor()
    {
        var viewModel = new MainWindowViewModel(
            restartOnExitWhenUnlocked: true,
            updateRestartSetting: _ => Task.CompletedTask,
            requestLock: () => Task.CompletedTask);
        DailyUsageRestrictionViewModel wednesday = viewModel.WeekdayRestrictions.Single(
            day => day.DayOfWeek == DayOfWeek.Wednesday);
        wednesday.IsRestrictionEnabled = true;
        var window = new MainWindow(viewModel)
        {
            Left = -10_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Top = -10_000,
        };

        try
        {
            window.MainTabs.SelectedIndex = 1;
            window.Show();
            window.WeekdayRestrictionsGrid.BringIntoView();
            window.WeekdayRestrictionsGrid.ScrollIntoView(wednesday);
            window.WeekdayRestrictionsGrid.UpdateLayout();
            var column = (DataGridTemplateColumn)window.WeekdayRestrictionsGrid.Columns[1];
            var row = (DataGridRow)window.WeekdayRestrictionsGrid.ItemContainerGenerator
                .ContainerFromItem(wednesday);
            var presenter = FindVisualChild<DataGridCellsPresenter>(row);
            var cell = (DataGridCell)presenter.ItemContainerGenerator.ContainerFromIndex(1);
            var checkBox = FindVisualChild<CheckBox>(cell);

            Assert.IsTrue(checkBox.IsChecked);

            checkBox.IsChecked = false;

            Assert.IsFalse(wednesday.IsRestrictionEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    private static T FindVisualChild<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            System.Windows.DependencyObject child =
                System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindVisualChildOrDefault<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        throw new InvalidOperationException($"{typeof(T).Name} was not found.");
    }

    private static T? FindVisualChildOrDefault<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            System.Windows.DependencyObject child =
                System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindVisualChildOrDefault<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
