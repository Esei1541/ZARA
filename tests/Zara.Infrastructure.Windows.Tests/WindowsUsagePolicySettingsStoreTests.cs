using System.Text.Json.Nodes;
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
    public async Task SecondSaveReplacesBothPrimaryAndLastKnownGoodSettings()
    {
        UsagePolicySettings first = CreateSettings();
        UsagePolicySettings second = new(
            WeeklyUsageRestrictionSchedule.Default.WithRestriction(
                DayOfWeek.Thursday,
                new DailyUsageRestriction(
                    isEnabled: true,
                    startTime: new TimeOnly(22, 45),
                    releaseTime: new TimeOnly(6, 30))),
            new EmergencyUnlockSettings(durationMinutes: 5, sentenceCount: 1),
            Array.Empty<OutOfHoursReservation>());

        using (var store = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath))
        {
            await store.SaveAsync(first);
            await store.SaveAsync(second);
        }

        using (var restartedStore = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath))
        {
            AssertSettingsEqual(second, await restartedStore.LoadAsync());
        }

        File.Delete(GetSettingsFilePath());
        using var backupOnlyStore = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath);
        AssertSettingsEqual(second, await backupOnlyStore.LoadAsync());
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

    [TestMethod]
    public async Task LegacyDocumentWithoutWeeklyFieldsLoadsDisabledDefaultsAndEmptyUsage()
    {
        using var store = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath);
        await store.SaveAsync(CreateSettings());
        JsonObject document = await ReadDocumentAsync();
        JsonObject emergency = document["emergencyUnlock"]!.AsObject();
        emergency.Remove("weeklyLimitEnabled");
        emergency.Remove("weeklyResetDay");
        emergency.Remove("weeklyMaximumCount");
        document.Remove("emergencyUnlockUsage");
        await File.WriteAllTextAsync(GetSettingsFilePath(), document.ToJsonString());

        UsagePolicySettings loaded = await store.LoadAsync();

        Assert.IsFalse(loaded.EmergencyUnlock.WeeklyLimitEnabled);
        Assert.AreEqual(DayOfWeek.Sunday, loaded.EmergencyUnlock.WeeklyResetDay);
        Assert.AreEqual(3, loaded.EmergencyUnlock.WeeklyMaximumCount);
        Assert.AreEqual(0, loaded.EmergencyUnlockUsage.UsedCount);
        Assert.IsNull(loaded.EmergencyUnlockUsage.NextResetLocalTime);
    }

    [TestMethod]
    [DataRow("weeklyMaximumCount", 0)]
    [DataRow("weeklyMaximumCount", 100)]
    [DataRow("weeklyResetDay", 7)]
    public async Task InvalidWeeklySettingsUseLastKnownGoodSnapshot(string property, int invalidValue)
    {
        UsagePolicySettings expected = CreateSettings();
        using var store = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath);
        await store.SaveAsync(expected);
        JsonObject document = await ReadDocumentAsync();
        document["emergencyUnlock"]![property] = invalidValue;
        await File.WriteAllTextAsync(GetSettingsFilePath(), document.ToJsonString());

        AssertSettingsEqual(expected, await store.LoadAsync());
        Assert.IsFalse(File.Exists(GetSettingsFilePath()));
        Assert.AreEqual(1, Directory.EnumerateFiles(
            _settingsDirectoryPath, "usage-policy.corrupt-*.json").Count());
    }

    [TestMethod]
    [DataRow(100, "2026-09-25T00:00:00")]
    [DataRow(1, "2026-09-25T00:01:00")]
    [DataRow(1, null)]
    public async Task InvalidUsageUsesLastKnownGoodSnapshot(int usedCount, string? nextReset)
    {
        UsagePolicySettings expected = CreateSettings();
        using var store = new WindowsUsagePolicySettingsStore(_settingsDirectoryPath);
        await store.SaveAsync(expected);
        JsonObject document = await ReadDocumentAsync();
        JsonObject usage = document["emergencyUnlockUsage"]!.AsObject();
        usage["usedCount"] = usedCount;
        usage["nextResetLocalTime"] = nextReset;
        await File.WriteAllTextAsync(GetSettingsFilePath(), document.ToJsonString());

        AssertSettingsEqual(expected, await store.LoadAsync());
        Assert.IsFalse(File.Exists(GetSettingsFilePath()));
        Assert.AreEqual(1, Directory.EnumerateFiles(
            _settingsDirectoryPath, "usage-policy.corrupt-*.json").Count());
    }

    private async Task<JsonObject> ReadDocumentAsync() =>
        JsonNode.Parse(await File.ReadAllTextAsync(GetSettingsFilePath()))!.AsObject();

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
            new EmergencyUnlockSettings(
                durationMinutes: 17,
                sentenceCount: 4,
                weeklyLimitEnabled: true,
                weeklyResetDay: DayOfWeek.Friday,
                weeklyMaximumCount: 5),
            [
                new OutOfHoursReservation(
                    Guid.Parse("21DFAFA2-3FD3-4678-8B5C-2E5A13C57F49"),
                    new DateOnly(2026, 8, 17),
                    new TimeOnly(9, 0),
                    new TimeOnly(10, 30),
                    "병원 예약"),
            ],
            new EmergencyUnlockUsage(usedCount: 2,
                nextResetLocalTime: new DateTime(2026, 9, 25)));
    }

    private static void AssertSettingsEqual(UsagePolicySettings expected, UsagePolicySettings actual)
    {
        Assert.AreEqual(expected.WeeklySchedule, actual.WeeklySchedule);
        Assert.AreEqual(expected.EmergencyUnlock, actual.EmergencyUnlock);
        Assert.AreEqual(expected.EmergencyUnlockUsage, actual.EmergencyUnlockUsage);
        CollectionAssert.AreEqual(expected.Reservations, actual.Reservations);
    }

    private string GetSettingsFilePath() =>
        Path.Combine(_settingsDirectoryPath, "usage-policy.json");

    private string GetLastKnownGoodFilePath() =>
        Path.Combine(_settingsDirectoryPath, "usage-policy.last-known-good.json");
}
