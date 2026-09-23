using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop;

public partial class WeeklyScheduleView : System.Windows.Controls.UserControl
{
    public WeeklyScheduleView() => InitializeComponent();

    private void TimeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        TimeSlider.IsSnapToTickEnabled = true;

    private void TimeSlider_EndMouseAdjustment(object sender, System.Windows.Input.MouseEventArgs e) =>
        TimeSlider.IsSnapToTickEnabled = false;

    private void TimeSlider_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        TimeSlider.IsSnapToTickEnabled = false;

    private void TimeInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TimeSelectionViewModel { IsValid: true } time })
        {
            time.Set(time.ToTimeOnly());
        }
    }
}
