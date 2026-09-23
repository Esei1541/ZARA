using Zara.Core.UsagePolicy;

namespace Zara.Core.Tests;

[TestClass]
public sealed class EmergencyUnlockUsageTests
{
    private static readonly DateTime SundayNoon = new(2026, 9, 20, 12, 0, 0);

    [TestMethod]
    public void WeeklyLimitDefaultsToDisabledSundayAndThreeUses()
    {
        EmergencyUnlockSettings settings = EmergencyUnlockSettings.Default;

        Assert.IsFalse(settings.WeeklyLimitEnabled);
        Assert.AreEqual(DayOfWeek.Sunday, settings.WeeklyResetDay);
        Assert.AreEqual(3, settings.WeeklyMaximumCount);
        Assert.AreEqual(0, EmergencyUnlockUsage.Empty.UsedCount);
        Assert.IsNull(EmergencyUnlockUsage.Empty.NextResetLocalTime);
        Assert.IsNull(EmergencyUnlockUsage.Empty.GetRemainingCount(settings, SundayNoon));
    }

    [TestMethod]
    public void EverySelectedWeekdayGetsItsNextLocalMidnight()
    {
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            EmergencyUnlockSettings settings = Enabled(day);
            EmergencyUnlockUsage usage = EmergencyUnlockUsage.Empty.Refresh(settings, SundayNoon);
            int daysUntilReset = day == DayOfWeek.Sunday ? 7 : (int)day;

            Assert.AreEqual(SundayNoon.Date.AddDays(daysUntilReset), usage.NextResetLocalTime,
                $"Reset day: {day}");
            Assert.AreEqual<int?>(3, usage.GetRemainingCount(settings, SundayNoon),
                $"Reset day: {day}");
        }
    }

    [TestMethod]
    public void ExactlyAtResetMidnightRefillsOnceAndDoesNotCarryUnusedAllowances()
    {
        EmergencyUnlockSettings settings = Enabled(DayOfWeek.Sunday);
        DateTime boundary = new(2026, 9, 27);
        EmergencyUnlockUsage usage = new(usedCount: 2, nextResetLocalTime: boundary);

        Assert.AreEqual<int?>(1, usage.GetRemainingCount(settings, boundary.AddTicks(-1)));
        EmergencyUnlockUsage refreshed = usage.Refresh(settings, boundary);
        Assert.AreEqual(0, refreshed.UsedCount);
        Assert.AreEqual<int?>(3, refreshed.GetRemainingCount(settings, boundary));
        Assert.AreEqual(new DateTime(2026, 10, 4), refreshed.NextResetLocalTime);
        Assert.AreSame(refreshed, refreshed.Refresh(settings, boundary.AddHours(1)));
    }

    [TestMethod]
    public void PassingSeveralWeeksAndYearBoundaryOnlyStartsCurrentPeriod()
    {
        EmergencyUnlockSettings settings = Enabled(DayOfWeek.Sunday);
        EmergencyUnlockUsage usage = new(usedCount: 3, nextResetLocalTime: new DateTime(2026, 9, 27));

        EmergencyUnlockUsage refreshed = usage.Refresh(settings, new DateTime(2026, 12, 31, 12, 0, 0));

        Assert.AreEqual(0, refreshed.UsedCount);
        Assert.AreEqual(new DateTime(2027, 1, 3), refreshed.NextResetLocalTime);
        Assert.AreEqual<int?>(3, refreshed.GetRemainingCount(settings, new DateTime(2026, 12, 31, 12, 0, 0)));
    }

    [TestMethod]
    public void EarlierClockTimeDoesNotRefillOrMoveSavedBoundary()
    {
        EmergencyUnlockSettings settings = Enabled(DayOfWeek.Sunday);
        EmergencyUnlockUsage usage = new(usedCount: 2, nextResetLocalTime: new DateTime(2026, 9, 27));

        EmergencyUnlockUsage refreshed = usage.Refresh(settings, new DateTime(2026, 9, 13));

        Assert.AreSame(usage, refreshed);
        Assert.AreEqual<int?>(1, refreshed.GetRemainingCount(settings, new DateTime(2026, 9, 13)));
    }

    [TestMethod]
    public void ConsumeStopsAtMaximumAndChangedMaximumUsesExistingConsumption()
    {
        EmergencyUnlockSettings settings = Enabled(DayOfWeek.Sunday);
        EmergencyUnlockUsage usage = EmergencyUnlockUsage.Empty.Refresh(settings, SundayNoon);

        for (int count = 1; count <= 3; count++)
        {
            usage = usage.Consume(settings, SundayNoon);
            Assert.AreEqual<int?>(3 - count, usage.GetRemainingCount(settings, SundayNoon));
        }

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => usage.Consume(settings, SundayNoon));
        DateTime? existingBoundary = usage.NextResetLocalTime;
        EmergencyUnlockSettings raised = Enabled(DayOfWeek.Sunday, maximumCount: 5);
        EmergencyUnlockUsage raisedUsage = usage.ChangeSettings(settings, raised, SundayNoon);
        Assert.AreEqual<int?>(2, raisedUsage.GetRemainingCount(raised, SundayNoon));
        Assert.AreEqual(existingBoundary, raisedUsage.NextResetLocalTime);

        EmergencyUnlockSettings lowered = Enabled(DayOfWeek.Sunday, maximumCount: 1);
        EmergencyUnlockUsage loweredUsage = raisedUsage.ChangeSettings(raised, lowered, SundayNoon);
        Assert.AreEqual<int?>(0, loweredUsage.GetRemainingCount(lowered, SundayNoon));
        Assert.AreEqual(3, loweredUsage.UsedCount);
    }

    [TestMethod]
    public void ChangedWeekdayMovesOnlyTheFutureBoundary()
    {
        EmergencyUnlockSettings sunday = Enabled(DayOfWeek.Sunday);
        EmergencyUnlockSettings monday = Enabled(DayOfWeek.Monday);
        EmergencyUnlockUsage usage = new(usedCount: 2, nextResetLocalTime: new DateTime(2026, 9, 27));

        EmergencyUnlockUsage changed = usage.ChangeSettings(sunday, monday, SundayNoon);

        Assert.AreEqual(2, changed.UsedCount);
        Assert.AreEqual<int?>(1, changed.GetRemainingCount(monday, SundayNoon));
        Assert.AreEqual(new DateTime(2026, 9, 21), changed.NextResetLocalTime);
        Assert.AreSame(changed, changed.ChangeSettings(monday, monday, SundayNoon));
    }

    [TestMethod]
    public void DisabledUseDoesNotChargeAndReenableKeepsCountUntilBoundary()
    {
        EmergencyUnlockSettings enabled = Enabled(DayOfWeek.Sunday);
        EmergencyUnlockSettings disabled = EmergencyUnlockSettings.Default;
        EmergencyUnlockUsage usage = new(usedCount: 2, nextResetLocalTime: new DateTime(2026, 9, 27));

        EmergencyUnlockUsage whileDisabled = usage.ChangeSettings(enabled, disabled, SundayNoon)
            .Consume(disabled, SundayNoon);

        Assert.AreEqual(2, whileDisabled.UsedCount);
        Assert.IsNull(whileDisabled.GetRemainingCount(disabled, SundayNoon));
        Assert.AreEqual<int?>(1, whileDisabled.ChangeSettings(disabled, enabled, SundayNoon)
            .GetRemainingCount(enabled, SundayNoon));
        Assert.AreEqual<int?>(3, whileDisabled.ChangeSettings(
            disabled, enabled, new DateTime(2026, 9, 28))
            .GetRemainingCount(enabled, new DateTime(2026, 9, 28)));
    }

    [TestMethod]
    public void InvalidWeeklySettingsAndUsageAreRejected()
    {
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Enabled(DayOfWeek.Sunday, 0));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Enabled(DayOfWeek.Sunday, 100));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Enabled((DayOfWeek)7));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new EmergencyUnlockUsage(-1));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new EmergencyUnlockUsage(100));
        _ = Assert.ThrowsExactly<ArgumentException>(() => new EmergencyUnlockUsage(1));
        _ = Assert.ThrowsExactly<ArgumentException>(() => new EmergencyUnlockUsage(
            1, new DateTime(2026, 9, 27, 0, 1, 0)));
    }

    private static EmergencyUnlockSettings Enabled(
        DayOfWeek day,
        int maximumCount = 3) =>
        new(10, 3, weeklyLimitEnabled: true, weeklyResetDay: day,
            weeklyMaximumCount: maximumCount);
}
