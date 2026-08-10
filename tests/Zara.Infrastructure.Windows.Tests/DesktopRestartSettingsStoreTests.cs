using System.Text.Json;
using Zara.Infrastructure.Windows;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class DesktopRestartSettingsStoreTests
{
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
    public async Task MissingDocumentReturnsDefaultEnabled()
    {
        using var store = new WindowsDesktopRestartSettingsStore(_settingsDirectoryPath);

        DesktopRestartSettings settings = await store.LoadAsync();

        Assert.IsTrue(settings.RestartOnExitWhenUnlocked);
        Assert.IsFalse(File.Exists(GetSettingsFilePath()));
    }

    [TestMethod]
    public async Task DisabledSettingRoundTripsWithExactJsonProperty()
    {
        using var store = new WindowsDesktopRestartSettingsStore(_settingsDirectoryPath);
        var expected = new DesktopRestartSettings
        {
            RestartOnExitWhenUnlocked = false,
        };

        await store.SaveAsync(expected);
        DesktopRestartSettings actual = await store.LoadAsync();

        Assert.IsFalse(actual.RestartOnExitWhenUnlocked);
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(GetSettingsFilePath()));
        JsonProperty property = document.RootElement.EnumerateObject().Single();
        Assert.AreEqual("restartOnExitWhenUnlocked", property.Name);
        Assert.AreEqual(JsonValueKind.False, property.Value.ValueKind);
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
