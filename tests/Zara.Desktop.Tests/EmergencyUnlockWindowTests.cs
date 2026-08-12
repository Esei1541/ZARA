using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Zara.Application.UsagePolicy;
using Zara.Desktop.Overlays;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class EmergencyUnlockWindowTests
{
    private static readonly string[] ExpectedRunTexts =
        ["열네살 때에 소", "꿉", "질 같은 장가를 갔", "고"];

    [STATestMethod]
    public void EnteredTextRendersMatchedMismatchedAndPendingColors()
    {
        var challenge = new EmergencyUnlockChallenge(
            ["열네살 때에 소꼽질 같은 장가를 갔고"]);
        var window = new EmergencyUnlockWindow(
            challenge,
            _ => Task.FromResult(false));

        try
        {
            var input = (TextBox)window.FindName("InputTextBox");
            var challengeText = (TextBlock)window.FindName("ChallengeTextBlock");
            var matchedBrush = (Brush)window.FindResource("EmergencyUnlockMatchedTextBrush");
            var mismatchedBrush = (Brush)window.FindResource(
                "EmergencyUnlockMismatchedTextBrush");

            input.Text = "열네살 때에 소꿉질 같은 장가를 갔";

            Run[] runs = challengeText.Inlines.OfType<Run>().ToArray();
            CollectionAssert.AreEqual(
                ExpectedRunTexts,
                runs.Select(run => run.Text).ToArray());
            Assert.AreSame(matchedBrush, runs[0].Foreground);
            Assert.AreSame(mismatchedBrush, runs[1].Foreground);
            Assert.AreSame(matchedBrush, runs[2].Foreground);
            Assert.AreSame(challengeText.Foreground, runs[3].Foreground);
        }
        finally
        {
            window.Close();
        }
    }
}
