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
        ["열네살 때에 소", "꼽", "질 같은 장가를 갔", "고"];

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

    [STATestMethod]
    public void MismatchedAndExcessInputNeverChangesTheDisplayedChallenge()
    {
        var challenge = new EmergencyUnlockChallenge(["나는 날마다 이불을 뒤집어썼고"]);
        var window = new EmergencyUnlockWindow(
            challenge,
            _ => Task.FromResult(false));

        try
        {
            var input = (TextBox)window.FindName("InputTextBox");
            var challengeText = (TextBlock)window.FindName("ChallengeTextBlock");
            var mismatchedBrush = (Brush)window.FindResource(
                "EmergencyUnlockMismatchedTextBrush");

            input.Text = "s";

            Run[] runs = challengeText.Inlines.OfType<Run>().ToArray();
            Assert.AreEqual(challenge.ExpectedText, string.Concat(runs.Select(run => run.Text)));
            Assert.AreEqual("나", runs[0].Text);
            Assert.AreSame(mismatchedBrush, runs[0].Foreground);

            input.Text = challenge.ExpectedText + "초과";

            Assert.AreEqual(
                challenge.ExpectedText,
                string.Concat(challengeText.Inlines.OfType<Run>().Select(run => run.Text)));
        }
        finally
        {
            window.Close();
        }
    }

    [STATestMethod]
    public void FailedSubmissionExplainsTheSpecificInputProblem()
    {
        AssertSubmissionMessage("가나다", "가나", "예시문을 끝까지 입력하세요.");
        AssertSubmissionMessage("가나다", "가라다", "빨간색으로 표시된 부분을 확인하세요.");
        AssertSubmissionMessage("가나다", "가나다라", "예시문 뒤에 추가로 입력한 내용을 지우세요.");
        AssertSubmissionMessage(
            "가나다",
            "가나다",
            "문장은 모두 일치하지만 긴급 해제를 시작하지 못했습니다. 다시 시도하세요.");
    }

    private static void AssertSubmissionMessage(
        string expectedText,
        string enteredText,
        string expectedMessage)
    {
        var challenge = new EmergencyUnlockChallenge([expectedText]);
        var window = new EmergencyUnlockWindow(
            challenge,
            _ => Task.FromResult(false));

        try
        {
            var input = (TextBox)window.FindName("InputTextBox");
            var completeButton = (Button)window.FindName("CompleteButton");
            var validationMessage = (TextBlock)window.FindName("ValidationMessage");

            input.Text = enteredText;
            completeButton.RaiseEvent(
                new System.Windows.RoutedEventArgs(Button.ClickEvent));

            Assert.AreEqual(expectedMessage, validationMessage.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
