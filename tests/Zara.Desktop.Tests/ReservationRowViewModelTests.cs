using Zara.Core.UsagePolicy;
using Zara.Desktop.ViewModels;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class ReservationRowViewModelTests
{
    [TestMethod]
    public void OvernightRowNamesTheFollowingDayAndTracksBothDates()
    {
        var row = new ReservationRowViewModel(new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 30), new TimeOnly(23, 30), new TimeOnly(0, 30), string.Empty));

        Assert.AreEqual("23:30 → 다음 날 00:30", row.TimeRange);
        row.UpdatePresentation(new DateTime(2026, 9, 30, 23, 30, 0));
        Assert.IsTrue(row.IsActive);
        row.UpdatePresentation(new DateTime(2026, 10, 1));
        Assert.AreEqual("적용 중", row.ActiveStatusText);
        row.UpdatePresentation(new DateTime(2026, 10, 1, 0, 30, 0));
        Assert.IsFalse(row.IsActive);
    }

    [TestMethod]
    public void SameDayRowPreservesItsTimeRange()
    {
        var row = new ReservationRowViewModel(new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 30), new TimeOnly(0, 0), new TimeOnly(1, 0), string.Empty));

        Assert.AreEqual("00:00 → 01:00", row.TimeRange);
    }
}
