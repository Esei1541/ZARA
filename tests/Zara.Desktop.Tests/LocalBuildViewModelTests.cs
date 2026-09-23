#if LOCAL_BUILD_UPDATES
using Zara.Application.LocalBuilds;
using Zara.Desktop.LocalBuilds;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class LocalBuildViewModelTests
{
    [TestMethod]
    public async Task LoadPreservesBackendOrderAndDisablesOnlyTheExactCurrentBuild()
    {
        LocalBuildInfo current = Build("build-current", "1.0.1", "Staging", minute: 2);
        var updates = new RecordingUpdates
        {
            Catalog = new LocalBuildCatalog(
                "C:\\ZaraBuilds",
                current,
                [
                    new LocalBuildEntry(Build("build-new", "1.0.1", "Staging", minute: 3), null),
                    new LocalBuildEntry(current, null),
                    new LocalBuildEntry(Build("BUILD-CURRENT", "1.0.1", "Debug", minute: 1), null),
                    new LocalBuildEntry(
                        new LocalBuildInfo(
                            "build-broken",
                            string.Empty,
                            string.Empty,
                            DateTimeOffset.MinValue,
                            string.Empty,
                            string.Empty),
                        "build.json을 읽을 수 없습니다."),
                ],
                Message: null),
        };
        using var viewModel = CreateViewModel(updates);

        await viewModel.EnterAsync();

        Assert.HasCount(4, viewModel.Builds);
        Assert.AreEqual("build-new", viewModel.Builds[0].BuildId);
        Assert.AreEqual("build-current", viewModel.Builds[1].BuildId);
        Assert.AreEqual("BUILD-CURRENT", viewModel.Builds[2].BuildId);
        Assert.AreEqual("build-broken", viewModel.Builds[3].BuildId);
        Assert.IsFalse(viewModel.Builds[0].IsCurrent);
        Assert.IsTrue(viewModel.Builds[0].CanInstall);
        Assert.IsTrue(viewModel.Builds[1].IsCurrent);
        Assert.IsFalse(viewModel.Builds[1].CanInstall);
        Assert.IsFalse(viewModel.Builds[2].IsCurrent);
        Assert.IsTrue(viewModel.Builds[2].CanInstall);
        Assert.AreEqual("확인 불가", viewModel.Builds[3].VersionName);
        Assert.AreEqual("확인 불가", viewModel.Builds[3].CreatedAtText);
        Assert.IsFalse(viewModel.Builds[3].CanInstall);
    }

    [TestMethod]
    public async Task DirectorySelectionPersistsThroughTheUseCaseAndReloadsTheCatalog()
    {
        var updates = new RecordingUpdates
        {
            Catalog = EmptyCatalog("C:\\OldBuilds"),
            CatalogAfterDirectoryChange = EmptyCatalog("D:\\NewBuilds"),
        };
        string? pickerInput = null;
        using var viewModel = new LocalBuildViewModel(
            updates,
            currentDirectory =>
            {
                pickerInput = currentDirectory;
                return "D:\\NewBuilds";
            },
            _ => true);
        await viewModel.EnterAsync();

        await viewModel.ChangeDirectoryAsync();

        Assert.AreEqual("C:\\OldBuilds", pickerInput);
        Assert.HasCount(1, updates.ChangedDirectories);
        Assert.AreEqual(@"D:\NewBuilds", updates.ChangedDirectories[0]);
        Assert.AreEqual(2, updates.LoadCount);
        Assert.AreEqual("D:\\NewBuilds", viewModel.BuildsDirectory);
    }

    [TestMethod]
    public async Task InstallAllowsAnotherBuildWithTheSameVersionAndReportsOnlyInstallerStart()
    {
        LocalBuildInfo current = Build("current", "1.0.1", "Staging", minute: 2);
        LocalBuildInfo previous = Build("previous", "1.0.1", "Debug", minute: 1);
        var updates = new RecordingUpdates
        {
            Catalog = new LocalBuildCatalog(
                "C:\\Builds",
                current,
                [new LocalBuildEntry(previous, null), new LocalBuildEntry(current, null)],
                Message: null),
            InstallResult = true,
        };
        LocalBuildItemViewModel? confirmedBuild = null;
        using var viewModel = new LocalBuildViewModel(
            updates,
            _ => null,
            build =>
            {
                confirmedBuild = build;
                return true;
            });
        await viewModel.EnterAsync();
        viewModel.SelectedBuild = viewModel.Builds[0];

        await viewModel.InstallSelectedBuildAsync();

        Assert.AreSame(viewModel.Builds[0], confirmedBuild);
        Assert.HasCount(1, updates.InstalledBuildIds);
        Assert.AreEqual("previous", updates.InstalledBuildIds[0]);
        StringAssert.Contains(viewModel.StatusMessage, "설치 관리자를 시작했습니다");
        Assert.IsFalse(viewModel.StatusMessage!.Contains("완료", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ElevationCancellationDoesNotReportAnInstallFailureOrCompletion()
    {
        var updates = new RecordingUpdates
        {
            Catalog = new LocalBuildCatalog(
                "C:\\Builds",
                CurrentBuild: null,
                [new LocalBuildEntry(Build("selected", "1.0.0", "Debug", minute: 1), null)],
                Message: null),
            InstallResult = false,
        };
        using var viewModel = CreateViewModel(updates);
        await viewModel.EnterAsync();
        viewModel.SelectedBuild = viewModel.Builds.Single();

        await viewModel.InstallSelectedBuildAsync();

        StringAssert.Contains(viewModel.StatusMessage, "취소");
        Assert.IsNull(viewModel.ErrorMessage);
        Assert.IsFalse(viewModel.StatusMessage!.Contains("완료", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BusyLoadBlocksEveryOtherScreenOperation()
    {
        var loadSource = new TaskCompletionSource<LocalBuildCatalog>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new RecordingUpdates { PendingLoad = loadSource };
        using var viewModel = CreateViewModel(updates);

        Task load = viewModel.EnterAsync();
        await updates.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(viewModel.IsBusy);
        Assert.IsFalse(viewModel.CanInteract);
        Assert.IsFalse(viewModel.RefreshCommand.CanExecute(parameter: null));
        Assert.IsFalse(viewModel.ChangeDirectoryCommand.CanExecute(parameter: null));
        Assert.IsFalse(viewModel.InstallSelectedBuildCommand.CanExecute(parameter: null));
        await viewModel.RefreshAsync();
        await viewModel.ChangeDirectoryAsync();
        Assert.AreEqual(1, updates.LoadCount);
        Assert.IsEmpty(updates.ChangedDirectories);

        loadSource.SetResult(EmptyCatalog("C:\\Builds"));
        await load;
        Assert.IsFalse(viewModel.IsBusy);
    }

    [TestMethod]
    public async Task DisposeCancelsThePendingLoadWithoutShowingAnError()
    {
        var updates = new RecordingUpdates { WaitForCancellation = true };
        var viewModel = CreateViewModel(updates);
        Task load = viewModel.EnterAsync();
        await updates.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Dispose();
        await load;

        Assert.IsNull(viewModel.ErrorMessage);
        Assert.IsFalse(viewModel.CanInteract);
    }

    [TestMethod]
    public async Task LoadFailureKeepsTheCauseReadableOnTheScreen()
    {
        var updates = new RecordingUpdates
        {
            LoadException = new InvalidOperationException("build.json이 손상되었습니다."),
        };
        using var viewModel = CreateViewModel(updates);

        await viewModel.EnterAsync();

        StringAssert.Contains(viewModel.ErrorMessage, "빌드 목록을 불러오지 못했습니다.");
        StringAssert.Contains(viewModel.ErrorMessage, "build.json이 손상되었습니다.");
    }

    private static LocalBuildViewModel CreateViewModel(RecordingUpdates updates) =>
        new(updates, _ => null, _ => true);

    private static LocalBuildCatalog EmptyCatalog(string directory) =>
        new(directory, CurrentBuild: null, Array.Empty<LocalBuildEntry>(), Message: null);

    private static LocalBuildInfo Build(
        string buildId,
        string version,
        string configuration,
        int minute) =>
        new(
            buildId,
            version,
            configuration,
            new DateTimeOffset(2026, 9, 23, 10, minute, 0, TimeSpan.FromHours(9)),
            "260923-staging-build-updates",
            "f27d734");

    private sealed class RecordingUpdates : ILocalBuildUpdates
    {
        internal LocalBuildCatalog Catalog { get; set; } = EmptyCatalog("C:\\Builds");
        internal LocalBuildCatalog? CatalogAfterDirectoryChange { get; set; }
        internal TaskCompletionSource<LocalBuildCatalog>? PendingLoad { get; set; }
        internal Exception? LoadException { get; set; }
        internal bool WaitForCancellation { get; set; }
        internal bool InstallResult { get; set; } = true;
        internal int LoadCount { get; private set; }
        internal List<string> ChangedDirectories { get; } = [];
        internal List<string> InstalledBuildIds { get; } = [];
        internal TaskCompletionSource LoadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LocalBuildCatalog> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LoadStarted.TrySetResult();
            if (LoadException is not null)
            {
                throw LoadException;
            }

            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (PendingLoad is not null)
            {
                return await PendingLoad.Task.WaitAsync(cancellationToken);
            }

            return Catalog;
        }

        public Task ChangeDirectoryAsync(
            string directory,
            CancellationToken cancellationToken = default)
        {
            ChangedDirectories.Add(directory);
            if (CatalogAfterDirectoryChange is not null)
            {
                Catalog = CatalogAfterDirectoryChange;
            }

            return Task.CompletedTask;
        }

        public Task<bool> InstallAsync(
            string buildId,
            CancellationToken cancellationToken = default)
        {
            InstalledBuildIds.Add(buildId);
            return Task.FromResult(InstallResult);
        }
    }
}
#endif
