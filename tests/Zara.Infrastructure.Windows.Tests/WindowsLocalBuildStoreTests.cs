#if LOCAL_BUILD_UPDATES
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zara.Application.LocalBuilds;
using Zara.Infrastructure.Windows.LocalBuilds;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsLocalBuildStoreTests
{
    private string _root = null!;
    private string _application = null!;
    private string _settings = null!;
    private string _builds = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "zara-local-build-tests", Guid.NewGuid().ToString("N"));
        _application = Path.Combine(_root, "installed");
        _settings = Path.Combine(_root, "settings");
        _builds = Path.Combine(_root, "한글 빌드");
        Directory.CreateDirectory(_application);
        Directory.CreateDirectory(_settings);
        Directory.CreateDirectory(_builds);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task UnpackagedBuildCanChooseCollectionWithoutTouchingProductSettings()
    {
        string productSettings = Path.Combine(_settings, "settings.json");
        await File.WriteAllTextAsync(productSettings, "keep this file");
        var store = CreateStore();
        LocalBuildCatalog first = await store.LoadAsync();
        Assert.IsNull(first.CurrentBuild);
        Assert.AreEqual(string.Empty, first.BuildsDirectory);

        await store.ChangeDirectoryAsync(_builds);
        LocalBuildCatalog reopened = await CreateStore().LoadAsync();
        Assert.AreEqual(_builds, reopened.BuildsDirectory);
        Assert.AreEqual("keep this file", await File.ReadAllTextAsync(productSettings));
        Assert.HasCount(0, Directory.GetFiles(_settings, "*.tmp"));
    }

    [TestMethod]
    public async Task SameVersionBuildsAreSortedByTimestampAndOlderBuildRemainsInstallable()
    {
        LocalBuildManifest older = await AddBuildAsync("older", DateTimeOffset.Parse("2026-09-23T09:00:00Z", CultureInfo.InvariantCulture));
        LocalBuildManifest newer = await AddBuildAsync("newer", DateTimeOffset.Parse("2026-09-23T10:00:00Z", CultureInfo.InvariantCulture));
        await WriteInstalledIdentityAsync(newer);
        var store = CreateStore();

        LocalBuildCatalog catalog = await store.LoadAsync();

        Assert.HasCount(2, catalog.Builds);
        Assert.AreEqual("newer", catalog.Builds[0].Build.BuildId);
        Assert.AreEqual("older", catalog.Builds[1].Build.BuildId);
        Assert.AreEqual("newer", catalog.CurrentBuild?.BuildId);
        Assert.AreEqual(catalog.Builds[0].Build.VersionName, catalog.Builds[1].Build.VersionName);
        Assert.AreEqual(Path.Combine(_builds, "older", older.InstallerFileName),
            await store.ValidateInstallerAsync("older"));
    }

    [TestMethod]
    public async Task PendingDirectoriesAreHiddenAndBrokenCompletedEntriesAreDisabled()
    {
        Directory.CreateDirectory(Path.Combine(_builds, "pending"));
        LocalBuildManifest missing = await AddBuildAsync("missing", DateTimeOffset.UtcNow);
        File.Delete(Path.Combine(_builds, "missing", missing.InstallerFileName));
        Directory.CreateDirectory(Path.Combine(_builds, "invalid"));
        await File.WriteAllTextAsync(Path.Combine(_builds, "invalid", "build.json"), "{ broken");
        var store = CreateStore();
        await store.ChangeDirectoryAsync(_builds);

        LocalBuildCatalog catalog = await store.LoadAsync();

        Assert.HasCount(2, catalog.Builds);
        Assert.IsTrue(catalog.Builds.All(row => !row.CanInstall));
        Assert.IsFalse(catalog.Builds.Any(row => row.Build.BuildId == "pending"));
    }

    [TestMethod]
    public async Task FileChangedAfterListingIsRejectedBeforeInstallation()
    {
        LocalBuildManifest build = await AddBuildAsync("changed", DateTimeOffset.UtcNow);
        var store = CreateStore();
        await store.ChangeDirectoryAsync(_builds);
        Assert.IsTrue((await store.LoadAsync()).Builds.Single().CanInstall);
        await File.AppendAllTextAsync(Path.Combine(_builds, "changed", build.InstallerFileName), "changed");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ValidateInstallerAsync("changed"));
    }

    [TestMethod]
    public async Task ManifestCannotRedirectInstallationOutsideItsBuildDirectory()
    {
        LocalBuildManifest build = await AddBuildAsync("escape", DateTimeOffset.UtcNow);
        build.InstallerFileName = "..\\outside.exe";
        await WriteManifestAsync(Path.Combine(_builds, "escape", "build.json"), build);
        var store = CreateStore();
        await store.ChangeDirectoryAsync(_builds);

        Assert.IsFalse((await store.LoadAsync()).Builds.Single().CanInstall);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ValidateInstallerAsync("escape"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ValidateInstallerAsync("..\\escape"));
    }

    [TestMethod]
    public async Task ManifestChangedAfterListingIsRechecked()
    {
        LocalBuildManifest build = await AddBuildAsync("identity", DateTimeOffset.UtcNow);
        var store = CreateStore();
        await store.ChangeDirectoryAsync(_builds);
        _ = await store.LoadAsync();
        build.BuildId = "different";
        await WriteManifestAsync(Path.Combine(_builds, "identity", "build.json"), build);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ValidateInstallerAsync("identity"));
    }

    [TestMethod]
    public async Task SavedFolderOverridesBuildLocationAndSurvivesRestart()
    {
        LocalBuildManifest original = await AddBuildAsync("original", DateTimeOffset.UtcNow);
        await WriteInstalledIdentityAsync(original);
        string replacement = Path.Combine(_root, "다른 폴더");
        Directory.CreateDirectory(replacement);
        await CreateStore().ChangeDirectoryAsync(replacement);

        LocalBuildCatalog catalog = await CreateStore().LoadAsync();
        Assert.AreEqual(replacement, catalog.BuildsDirectory);
        Assert.AreEqual("original", catalog.CurrentBuild?.BuildId);
        Assert.HasCount(0, catalog.Builds);

        Directory.Delete(replacement);
        LocalBuildCatalog missing = await CreateStore().LoadAsync();
        Assert.AreEqual(replacement, missing.BuildsDirectory);
        Assert.IsNotNull(missing.Message);
        Assert.HasCount(0, missing.Builds);
    }

    [TestMethod]
    public async Task FailedFolderChangePreservesPreviousSelection()
    {
        var store = CreateStore();
        await store.ChangeDirectoryAsync(_builds);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.ChangeDirectoryAsync("relative"));
        Assert.AreEqual(_builds, (await CreateStore().LoadAsync()).BuildsDirectory);
    }

    [TestMethod]
    public async Task CorruptSavedLocationUsesInstalledDefaultAndReportsProblem()
    {
        LocalBuildManifest build = await AddBuildAsync("default", DateTimeOffset.UtcNow);
        await WriteInstalledIdentityAsync(build);
        await File.WriteAllTextAsync(Path.Combine(_settings, "local-build-settings.json"), "{ broken");

        LocalBuildCatalog catalog = await CreateStore().LoadAsync();
        Assert.AreEqual(_builds, catalog.BuildsDirectory);
        Assert.IsNotNull(catalog.Message);
        Assert.HasCount(1, catalog.Builds);
    }

    private WindowsLocalBuildStore CreateStore() => new(_application, _settings);

    private async Task<LocalBuildManifest> AddBuildAsync(string id, DateTimeOffset createdAt)
    {
        string directory = Path.Combine(_builds, id);
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.UTF8.GetBytes("installer fixture: " + id);
        var manifest = new LocalBuildManifest
        {
            SchemaVersion = 1,
            BuildId = id,
            VersionName = "1.0.1",
            Configuration = "Staging",
            CreatedAt = createdAt,
            Branch = "260923-test-builds",
            Commit = new string('a', 40),
            InstallerFileName = id + ".exe",
            InstallerSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        };
        await File.WriteAllBytesAsync(Path.Combine(directory, manifest.InstallerFileName), bytes);
        await WriteManifestAsync(Path.Combine(directory, "build.json"), manifest);
        return manifest;
    }

    private Task WriteInstalledIdentityAsync(LocalBuildManifest manifest)
    {
        manifest.BuildsDirectory = _builds;
        return WriteManifestAsync(Path.Combine(_application, "local-build.json"), manifest);
    }

    private static Task WriteManifestAsync(string path, LocalBuildManifest manifest) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, LocalBuildManifest.JsonOptions));
}
#endif
