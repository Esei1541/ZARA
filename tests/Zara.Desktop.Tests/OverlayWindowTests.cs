using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zara.Desktop.Overlays;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class OverlayWindowTests
{
    [STATestMethod]
    public void WeeklyEmergencyCountIsVisibleOnlyWhenEnabledAndZeroIsHighlighted()
    {
        var window = new OverlayWindow(
            requestSystemShutdown: () => Task.CompletedTask,
            requestEmergencyUnlock: () => Task.CompletedTask
#if DEBUG
            , requestDevelopmentUnlock: () => Task.CompletedTask
#endif
            );

        try
        {
            var remainingText = (TextBlock)window.FindName("EmergencyUnlockRemainingText");
            var emergencyButton = (Button)window.FindName("EmergencyUnlockButton");
            Assert.AreEqual(Visibility.Collapsed, remainingText.Visibility);

            window.SetEmergencyUnlockRemainingCount(2);
            Assert.AreEqual(Visibility.Visible, remainingText.Visibility);
            Assert.AreEqual("남은 긴급 해제 2회", remainingText.Text);

            window.SetEmergencyUnlockRemainingCount(0);
            window.SetEmergencyUnlockEnabled(isEnabled: false);
            Assert.AreEqual("남은 긴급 해제 0회", remainingText.Text);
            Assert.AreSame(Brushes.IndianRed, remainingText.Foreground);
            Assert.IsFalse(emergencyButton.IsEnabled);

            window.SetEmergencyUnlockRemainingCount(null);
            Assert.AreEqual(Visibility.Collapsed, remainingText.Visibility);
        }
        finally
        {
            window.CloseFromCoordinator();
        }
    }
}
