using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class TimeSelectionViewModelTests
{
    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(12, 0)]
    [DataRow(19, 30)]
    [DataRow(23, 59)]
    public void TwentyFourHourInputAndSliderRepresentTheSameMinute(int hour, int minute)
    {
        var viewModel = new TimeSelectionViewModel(use24HourClock: true);
        var expected = new TimeOnly(hour, minute);
        viewModel.Set(expected);
        Assert.AreEqual(expected, viewModel.ToTimeOnly());
        Assert.AreEqual(hour * 60d + minute, viewModel.MinutesSinceMidnight);
        viewModel.MinutesSinceMidnight = 1171;
        Assert.AreEqual("19", viewModel.HourText);
        Assert.AreEqual("31", viewModel.MinuteText);
    }

    [TestMethod]
    [DataRow("24", "00")]
    [DataRow("23", "60")]
    [DataRow("", "30")]
    public void InvalidTwentyFourHourInputIsRetainedAndNeverConvertedToAnotherTime(string hour, string minute)
    {
        var viewModel = new TimeSelectionViewModel(use24HourClock: true)
        {
            HourText = hour,
            MinuteText = minute,
        };
        Assert.IsFalse(viewModel.IsValid);
        Assert.AreEqual(hour, viewModel.HourText);
        Assert.AreEqual(minute, viewModel.MinuteText);
        Assert.ThrowsExactly<ArgumentException>(() => viewModel.ToTimeOnly());
    }

    [TestMethod]
    public void DirectTextInputConvertsTwelveHourClockBoundaries()
    {
        var viewModel = new TimeSelectionViewModel
        {
            Meridiem = "오전",
            HourText = "12",
            MinuteText = "59",
        };

        Assert.AreEqual(new TimeOnly(0, 59), viewModel.ToTimeOnly("시작 시각"));

        viewModel.Meridiem = "오후";
        Assert.AreEqual(new TimeOnly(12, 59), viewModel.ToTimeOnly("시작 시각"));

        viewModel.HourText = "1";
        viewModel.MinuteText = "0";
        Assert.AreEqual(new TimeOnly(13, 0), viewModel.ToTimeOnly("시작 시각"));
    }

    [TestMethod]
    [DataRow("", "0")]
    [DataRow("0", "0")]
    [DataRow("13", "0")]
    [DataRow("한", "0")]
    public void InvalidHourTextIdentifiesTheExactField(string hourText, string minuteText)
    {
        var viewModel = new TimeSelectionViewModel
        {
            HourText = hourText,
            MinuteText = minuteText,
        };

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => viewModel.ToTimeOnly("월요일 시작 시각"));

        Assert.AreEqual(
            "월요일 시작 시각의 시간은 1부터 12까지의 정수로 입력하세요.",
            exception.Message);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("60")]
    [DataRow("분")]
    public void InvalidMinuteTextIdentifiesTheExactField(string minuteText)
    {
        var viewModel = new TimeSelectionViewModel
        {
            HourText = "12",
            MinuteText = minuteText,
        };

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(
            () => viewModel.ToTimeOnly("화요일 해제 시각"));

        Assert.AreEqual(
            "화요일 해제 시각의 분은 0부터 59까지의 정수로 입력하세요.",
            exception.Message);
    }

    [TestMethod]
    public void LoadingPersistedTimeReplacesInvalidPartialInput()
    {
        var viewModel = new TimeSelectionViewModel
        {
            HourText = string.Empty,
            MinuteText = "99",
        };

        viewModel.Set(new TimeOnly(12, 5));

        Assert.AreEqual("오후", viewModel.Meridiem);
        Assert.AreEqual("12", viewModel.HourText);
        Assert.AreEqual("5", viewModel.MinuteText);
        Assert.AreEqual(new TimeOnly(12, 5), viewModel.ToTimeOnly());
    }
}
