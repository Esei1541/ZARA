using System.Text.Json;
using Zara.Infrastructure.Windows;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class DesktopRestartSettingsStoreTests
{
    private static readonly string[] SettingsPropertyNames =
    [
        "restartOnExitWhenUnlocked",
        "voiceReminder30Minutes",
        "voiceReminder10Minutes",
        "voiceReminder5Minutes",
        "voiceReminder1Minute",
    ];
    private string _settingsDirectoryPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _settingsDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "zara-restart-settings-tests",
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
    public async Task MissingDocumentReturnsEveryDefaultEnabled()
    {
        using var store = new WindowsDesktopRestartSettingsStore(_settingsDirectoryPath);

        DesktopRestartSettings settings = await store.LoadAsync();

        Assert.IsTrue(settings.RestartOnExitWhenUnlocked);
        Assert.IsTrue(settings.VoiceReminder30Minutes);
        Assert.IsTrue(settings.VoiceReminder10Minutes);
        Assert.IsTrue(settings.VoiceReminder5Minutes);
        Assert.IsTrue(settings.VoiceReminder1Minute);
        Assert.IsFalse(File.Exists(GetSettingsFilePath()));
    }

    [TestMethod]
    public async Task CompleteSettingsRoundTripWithExactJsonProperties()
    {
        using var store = new WindowsDesktopRestartSettingsStore(_settingsDirectoryPath);
        var expected = new DesktopRestartSettings
        {
            RestartOnExitWhenUnlocked = false,
            VoiceReminder30Minutes = false,
            VoiceReminder10Minutes = true,
            VoiceReminder5Minutes = false,
            VoiceReminder1Minute = true,
        };

        await store.SaveAsync(expected);
        DesktopRestartSettings actual = await store.LoadAsync();

        Assert.AreEqual(expected, actual);
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(GetSettingsFilePath()));
        Dictionary<string, JsonValueKind> properties = document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.ValueKind);
        CollectionAssert.AreEquivalent(
            SettingsPropertyNames,
            properties.Keys.ToArray());
        Assert.AreEqual(JsonValueKind.False, properties["restartOnExitWhenUnlocked"]);
        Assert.AreEqual(JsonValueKind.False, properties["voiceReminder30Minutes"]);
        Assert.AreEqual(JsonValueKind.True, properties["voiceReminder10Minutes"]);
        Assert.AreEqual(JsonValueKind.False, properties["voiceReminder5Minutes"]);
        Assert.AreEqual(JsonValueKind.True, properties["voiceReminder1Minute"]);
    }

    [TestMethod]
    public async Task ExistingDocumentWithoutReminderFieldsUsesEnabledDefaults()
    {
        await File.WriteAllTextAsync(
            GetSettingsFilePath(),
            """{"restartOnExitWhenUnlocked":false}""");
        using var store = new WindowsDesktopRestartSettingsStore(_settingsDirectoryPath);

        DesktopRestartSettings settings = await store.LoadAsync();

        Assert.IsFalse(settings.RestartOnExitWhenUnlocked);
        Assert.IsTrue(settings.VoiceReminder30Minutes);
        Assert.IsTrue(settings.VoiceReminder10Minutes);
        Assert.IsTrue(settings.VoiceReminder5Minutes);
        Assert.IsTrue(settings.VoiceReminder1Minute);
    }

    [TestMethod]
    public async Task PartiallySpecifiedReminderFieldsKeepOtherEnabledDefaults()
    {
        await File.WriteAllTextAsync(
            GetSettingsFilePath(),
            """{"voiceReminder10Minutes":false,"voiceReminder1Minute":false}""");
        using var store = new WindowsDesktopRestartSettingsStore(_settingsDirectoryPath);

        DesktopRestartSettings settings = await store.LoadAsync();

        Assert.IsTrue(settings.RestartOnExitWhenUnlocked);
        Assert.IsTrue(settings.VoiceReminder30Minutes);
        Assert.IsFalse(settings.VoiceReminder10Minutes);
        Assert.IsTrue(settings.VoiceReminder5Minutes);
        Assert.IsFalse(settings.VoiceReminder1Minute);
    }

    [TestMethod]
    public async Task CorruptJsonIsQuarantinedAndReturnsDefaultEnabled()
    {
        const string corruptJson = "{ not valid json";
        string settingsFilePath = GetSettingsFilePath();
        await File.WriteAllTextAsync(settingsFilePath, corruptJson);
        using var store = new WindowsDesktopRestartSettingsStore(_settingsDirectoryPath);

        DesktopRestartSettings settings = await store.LoadAsync();

        Assert.IsTrue(settings.RestartOnExitWhenUnlocked);
        Assert.IsFalse(File.Exists(settingsFilePath));
        string quarantinePath = Directory
            .EnumerateFiles(_settingsDirectoryPath, "settings.corrupt-*.json")
            .Single();
        Assert.AreEqual(corruptJson, await File.ReadAllTextAsync(quarantinePath));
    }

    [TestMethod]
    public async Task ConcurrentSaveThenLoadObservesSerializedSnapshot()
    {
        using var store = new WindowsDesktopRestartSettingsStore(_settingsDirectoryPath);
        var disabled = new DesktopRestartSettings
        {
            RestartOnExitWhenUnlocked = false,
        };

        Task save = store.SaveAsync(disabled);
        Task<DesktopRestartSettings> load = store.LoadAsync();

        await save;
        Assert.IsFalse((await load).RestartOnExitWhenUnlocked);
    }

    [TestMethod]
    public async Task PreCancelledSavePreservesExistingSnapshot()
    {
        using var store = new WindowsDesktopRestartSettingsStore(_settingsDirectoryPath);
        await store.SaveAsync(new DesktopRestartSettings
        {
            RestartOnExitWhenUnlocked = false,
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.SaveAsync(
                new DesktopRestartSettings
                {
                    RestartOnExitWhenUnlocked = true,
                },
                cancellation.Token));

        Assert.IsFalse((await store.LoadAsync()).RestartOnExitWhenUnlocked);
        Assert.IsEmpty(Directory.EnumerateFiles(_settingsDirectoryPath, "*.tmp"));
    }

    [TestMethod]
    public async Task FailedAtomicReplacePreservesExistingSnapshotAndRemovesTemporaryFile()
    {
        using var store = new WindowsDesktopRestartSettingsStore(_settingsDirectoryPath);
        await store.SaveAsync(new DesktopRestartSettings
        {
            RestartOnExitWhenUnlocked = false,
        });

        await using (var lockedSettings = new FileStream(
            GetSettingsFilePath(),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            await Assert.ThrowsExactlyAsync<IOException>(
                () => store.SaveAsync(new DesktopRestartSettings
                {
                    RestartOnExitWhenUnlocked = true,
                }));
        }

        Assert.IsFalse((await store.LoadAsync()).RestartOnExitWhenUnlocked);
        Assert.IsEmpty(Directory.EnumerateFiles(_settingsDirectoryPath, "*.tmp"));
    }

    private string GetSettingsFilePath() =>
        Path.Combine(_settingsDirectoryPath, "settings.json");
}
