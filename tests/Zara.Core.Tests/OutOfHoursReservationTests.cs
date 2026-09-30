using Zara.Core.UsagePolicy;

namespace Zara.Core.Tests;

[TestClass]
public sealed class OutOfHoursReservationTests
{
    [TestMethod]
    [DataRow(0, 23, 29, false)]
    [DataRow(0, 23, 30, true)]
    [DataRow(1, 0, 0, true)]
    [DataRow(1, 0, 29, true)]
    [DataRow(1, 0, 30, false)]
    [DataRow(2, 0, 0, false)]
    public void OvernightReservationContainsBothDatesWithAnExclusiveEnd(
        int dayOffset, int hour, int minute, bool active)
    {
        var reservation = new OutOfHoursReservation(Guid.NewGuid(), new DateOnly(2026, 9, 30),
            new TimeOnly(23, 30), new TimeOnly(0, 30), string.Empty);
        DateTime localNow = new DateTime(2026, 9, 30).AddDays(dayOffset).AddHours(hour).AddMinutes(minute);

        Assert.AreEqual(active, reservation.Contains(localNow));
        Assert.AreEqual(new DateTime(2026, 10, 1, 0, 30, 0), reservation.GetEndLocalTime());
    }

    [TestMethod]
    public void MidnightEndAdvancesAcrossTheYearBoundary()
    {
        var reservation = new OutOfHoursReservation(Guid.NewGuid(), new DateOnly(2026, 12, 31),
            new TimeOnly(23, 0), TimeOnly.MinValue, string.Empty);

        Assert.AreEqual(new DateTime(2027, 1, 1), reservation.GetEndLocalTime());
        Assert.IsTrue(reservation.Contains(new DateTime(2026, 12, 31, 23, 59, 59)));
        Assert.IsFalse(reservation.Contains(new DateTime(2027, 1, 1)));
    }

    [TestMethod]
    public void MatchingClockTimesRemainInvalid()
    {
        _ = Assert.ThrowsExactly<ArgumentException>(() => new OutOfHoursReservation(
            Guid.NewGuid(), new DateOnly(2026, 9, 30), new TimeOnly(1, 0), new TimeOnly(1, 0), string.Empty));
    }

    [TestMethod]
    public void OvernightReservationRejectsAnUnrepresentableEndDate()
    {
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OutOfHoursReservation(
            Guid.NewGuid(), DateOnly.MaxValue, new TimeOnly(23, 0), TimeOnly.MinValue, string.Empty));
    }
}
