using Zara.Application.UsagePolicy;

namespace Zara.Application.Tests;

[TestClass]
public sealed class EmergencyUnlockInputMatcherTests
{
    [TestMethod]
    public void CompareShowsEnteredMismatchAndPendingExpectedText()
    {
        var challenge = new EmergencyUnlockChallenge(
            ["열네살 때에 소꼽질 같은 장가를 갔고"]);
        var matcher = new EmergencyUnlockInputMatcher(challenge);

        EmergencyUnlockInputComparison comparison = matcher.Compare(
            "열네살 때에 소꿉질 같은 장가를 갔");

        Assert.IsFalse(comparison.IsExactMatch);
        Assert.IsFalse(comparison.HasExcessInput);
        CollectionAssert.AreEqual(
            new EmergencyUnlockInputSegment[]
            {
                new("열네살 때에 소", EmergencyUnlockInputState.Matched),
                new("꼽", EmergencyUnlockInputState.Mismatched),
                new("질 같은 장가를 갔", EmergencyUnlockInputState.Matched),
                new("고", EmergencyUnlockInputState.Pending),
            },
            comparison.Segments.ToArray());
        Assert.AreEqual(
            challenge.ExpectedText,
            string.Concat(comparison.Segments.Select(segment => segment.Text)));
    }

    [TestMethod]
    public void CompareTreatsWindowsAndLineFeedEndingsAsEquivalent()
    {
        var challenge = new EmergencyUnlockChallenge(["첫 번째 문장", "두 번째 문장"]);
        var matcher = new EmergencyUnlockInputMatcher(challenge);

        EmergencyUnlockInputComparison comparison = matcher.Compare(
            "첫 번째 문장\r\n두 번째 문장");

        Assert.IsTrue(comparison.IsExactMatch);
        Assert.IsFalse(comparison.HasExcessInput);
        Assert.IsTrue(EmergencyUnlockInputMatcher.IsExactMatch(
            challenge,
            "첫 번째 문장\r\n두 번째 문장"));
        CollectionAssert.AreEqual(
            new EmergencyUnlockInputSegment[]
            {
                new("첫 번째 문장\n두 번째 문장", EmergencyUnlockInputState.Matched),
            },
            comparison.Segments.ToArray());
    }

    [TestMethod]
    public void CompareKeepsSurrogatePairsAndCombiningSequencesWhole()
    {
        var challenge = new EmergencyUnlockChallenge(["A😀e\u0301끝"]);
        var matcher = new EmergencyUnlockInputMatcher(challenge);

        EmergencyUnlockInputComparison comparison = matcher.Compare("A😁e\u0301");

        Assert.IsFalse(comparison.IsExactMatch);
        Assert.IsFalse(comparison.HasExcessInput);
        CollectionAssert.AreEqual(
            new EmergencyUnlockInputSegment[]
            {
                new("A", EmergencyUnlockInputState.Matched),
                new("😀", EmergencyUnlockInputState.Mismatched),
                new("e\u0301", EmergencyUnlockInputState.Matched),
                new("끝", EmergencyUnlockInputState.Pending),
            },
            comparison.Segments.ToArray());
        Assert.AreEqual(
            challenge.ExpectedText,
            string.Concat(comparison.Segments.Select(segment => segment.Text)));
    }

    [TestMethod]
    public void CompareKeepsDisplayedChallengeWhenInputHasExcessText()
    {
        var challenge = new EmergencyUnlockChallenge(["완료"]);
        var matcher = new EmergencyUnlockInputMatcher(challenge);

        EmergencyUnlockInputComparison comparison = matcher.Compare("완료됨");

        Assert.IsFalse(comparison.IsExactMatch);
        Assert.IsTrue(comparison.HasExcessInput);
        CollectionAssert.AreEqual(
            new EmergencyUnlockInputSegment[]
            {
                new("완료", EmergencyUnlockInputState.Matched),
            },
            comparison.Segments.ToArray());
        Assert.AreEqual(
            challenge.ExpectedText,
            string.Concat(comparison.Segments.Select(segment => segment.Text)));
    }
}
