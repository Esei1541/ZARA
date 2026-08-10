using Zara.Core.UsagePolicy;
using Zara.Infrastructure.Windows;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsUsagePolicySettingsStoreTests
{
    private string _settingsDirectoryPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _settingsDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "zara-usage-policy-settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_settingsDirectoryPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_settingsDirectoryPath))
        {
            Directory.Delete(_settingsDirectoryPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task SavedSettingsLoadFromANewStoreInstance()
    {
        UsagePolicySettings expected = CreateSettings();

        using (var firstStore = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath))
        {
            await firstStore.SaveAsync(expected);
        }

        using var restartedStore = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath);
        UsagePolicySettings actual = await restartedStore.LoadAsync();

        AssertSettingsEqual(expected, actual);
        Assert.IsTrue(File.Exists(GetSettingsFilePath()));
        Assert.IsTrue(File.Exists(GetLastKnownGoodFilePath()));
    }

    [TestMethod]
    public async Task CorruptJsonIsQuarantinedAndReturnsDefaultWithoutLastKnownGood()
    {
        const string corruptJson = "{ not valid json";
        string settingsFilePath = GetSettingsFilePath();
        await File.WriteAllTextAsync(settingsFilePath, corruptJson);
        using var store = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath);

        UsagePolicySettings actual = await store.LoadAsync();

        Assert.AreSame(UsagePolicySettings.Default, actual);
        Assert.IsFalse(File.Exists(settingsFilePath));
        string quarantinePath = Directory
            .EnumerateFiles(_settingsDirectoryPath, "usage-policy.corrupt-*.json")
            .Single();
        Assert.AreEqual(corruptJson, await File.ReadAllTextAsync(quarantinePath));
    }

    [TestMethod]
    public async Task CorruptPrimaryJsonUsesLastKnownGoodSnapshot()
    {
        UsagePolicySettings expected = CreateSettings();
        using (var firstStore = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath))
        {
            await firstStore.SaveAsync(expected);
        }

        const string corruptJson = "{ not valid json";
        await File.WriteAllTextAsync(GetSettingsFilePath(), corruptJson);
        using var restartedStore = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath);

        UsagePolicySettings actual = await restartedStore.LoadAsync();

        AssertSettingsEqual(expected, actual);
        Assert.IsTrue(File.Exists(GetLastKnownGoodFilePath()));
        string quarantinePath = Directory
            .EnumerateFiles(_settingsDirectoryPath, "usage-policy.corrupt-*.json")
            .Single();
        Assert.AreEqual(corruptJson, await File.ReadAllTextAsync(quarantinePath));
    }

    private static UsagePolicySettings CreateSettings()
    {
        WeeklyUsageRestrictionSchedule weeklySchedule = UsagePolicySettings.Default.WeeklySchedule
            .WithRestriction(
                DayOfWeek.Monday,
                new DailyUsageRestriction(
                    isEnabled: true,
                    startTime: new TimeOnly(20, 30),
                    releaseTime: new TimeOnly(6, 15)))
            .WithRestriction(
                DayOfWeek.Friday,
                new DailyUsageRestriction(
                    isEnabled: true,
                    startTime: new TimeOnly(13, 0),
                    releaseTime: new TimeOnly(14, 30)));
        return new UsagePolicySettings(
            weeklySchedule,
            new EmergencyUnlockSettings(durationMinutes: 17, sentenceCount: 4),
            [
                new OutOfHoursReservation(
                    Guid.Parse("21DFAFA2-3FD3-4678-8B5C-2E5A13C57F49"),
                    new DateOnly(2026, 8, 17),
                    new TimeOnly(9, 0),
                    new TimeOnly(10, 30),
                    "병원 예약"),
            ]);
    }

    private static void AssertSettingsEqual(UsagePolicySettings expected, UsagePolicySettings actual)
    {
        Assert.AreEqual(expected.WeeklySchedule, actual.WeeklySchedule);
        Assert.AreEqual(expected.EmergencyUnlock, actual.EmergencyUnlock);
        CollectionAssert.AreEqual(expected.Reservations, actual.Reservations);
    }

    private string GetSettingsFilePath() =>
        Path.Combine(_settingsDirectoryPath, "usage-policy.json");

    private string GetLastKnownGoodFilePath() =>
        Path.Combine(_settingsDirectoryPath, "usage-policy.last-known-good.json");
}
