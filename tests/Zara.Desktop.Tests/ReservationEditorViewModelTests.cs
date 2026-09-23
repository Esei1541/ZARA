using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class ReservationEditorViewModelTests
{
    [TestMethod]
    public void DirectTimeInputCreatesReservationDraft()
    {
        var viewModel = new ReservationEditorViewModel
        {
            SelectedDate = new DateTime(2026, 8, 14),
            Memo = "야간 작업",
        };
        viewModel.StartTime.HourText = "19";
        viewModel.StartTime.MinuteText = "30";
        viewModel.EndTime.HourText = "21";
        viewModel.EndTime.MinuteText = "00";

        bool result = viewModel.TryCreateDraft(
            out ReservationDraft? draft,
            out string validationMessage);

        Assert.IsTrue(result);
        Assert.AreEqual(string.Empty, validationMessage);
        Assert.IsNotNull(draft);
        Assert.AreEqual(new DateOnly(2026, 8, 14), draft.Date);
        Assert.AreEqual(new TimeOnly(19, 30), draft.StartTime);
        Assert.AreEqual(new TimeOnly(21, 0), draft.EndTime);
        Assert.AreEqual("야간 작업", draft.Memo);
    }

    [TestMethod]
    public void EmptyMemoRemainsEmptyInReservationDraft()
    {
        var viewModel = new ReservationEditorViewModel();

        bool result = viewModel.TryCreateDraft(
            out ReservationDraft? draft,
            out string validationMessage);

        Assert.IsTrue(result);
        Assert.AreEqual(string.Empty, validationMessage);
        Assert.IsNotNull(draft);
        Assert.AreEqual(new TimeOnly(0, 0), draft.StartTime);
        Assert.AreEqual(new TimeOnly(1, 0), draft.EndTime);
        Assert.AreEqual(string.Empty, draft.Memo);
    }

    [TestMethod]
    public void MissingDateReturnsKoreanValidationMessage()
    {
        var viewModel = new ReservationEditorViewModel
        {
            SelectedDate = null,
        };

        bool result = viewModel.TryCreateDraft(
            out ReservationDraft? draft,
            out string validationMessage);

        Assert.IsFalse(result);
        Assert.IsNull(draft);
        Assert.AreEqual("날짜를 선택해주세요.", validationMessage);
    }

    [TestMethod]
    [DataRow("", "0", "시작 시각의 시간은 0부터 23까지의 정수로 입력하세요.")]
    [DataRow("24", "0", "시작 시각의 시간은 0부터 23까지의 정수로 입력하세요.")]
    [DataRow("12", "60", "시작 시각의 분은 0부터 59까지의 정수로 입력하세요.")]
    public void InvalidStartTimeReturnsFieldSpecificKoreanMessage(
        string hourText,
        string minuteText,
        string expectedMessage)
    {
        var viewModel = new ReservationEditorViewModel();
        viewModel.StartTime.HourText = hourText;
        viewModel.StartTime.MinuteText = minuteText;

        bool result = viewModel.TryCreateDraft(
            out ReservationDraft? draft,
            out string validationMessage);

        Assert.IsFalse(result);
        Assert.IsNull(draft);
        Assert.AreEqual(expectedMessage, validationMessage);
    }

    [TestMethod]
    public void InvalidEndTimeReturnsFieldSpecificKoreanMessage()
    {
        var viewModel = new ReservationEditorViewModel();
        viewModel.EndTime.MinuteText = "60";

        bool result = viewModel.TryCreateDraft(
            out ReservationDraft? draft,
            out string validationMessage);

        Assert.IsFalse(result);
        Assert.IsNull(draft);
        Assert.AreEqual(
            "종료 시각의 분은 0부터 59까지의 정수로 입력하세요.",
            validationMessage);
    }
}
