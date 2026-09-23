using Zara.Application.Updates;

namespace Zara.Application.Tests;

[TestClass]
public sealed class ReleaseUpdateUseCaseTests
{
    private static readonly ReleaseUpdate NewerRelease = new(
        new Version(1, 0, 2),
        "Release notes",
        new Uri("https://github.com/Esei1541/ZARA/releases/download/v1.0.2/ZARA-1.0.2-win-x64-Setup.exe"),
        new string('a', 64),
        10);

    [TestMethod]
    public async Task CurrentVersionIsPreservedAndCheckReturnsSourceRelease()
    {
        var currentVersion = new Version(1, 0, 1);
        var source = new TestSource { Latest = _ => Task.FromResult(NewerRelease) };
        var updates = new ReleaseUpdateUseCase(currentVersion, source, new TestInstaller());

        Assert.AreSame(currentVersion, updates.CurrentVersion);
        Assert.AreSame(NewerRelease, await updates.CheckAsync());
    }

    [TestMethod]
    public async Task InstallDownloadsThenLaunchesReturnedPath()
    {
        var calls = new List<string>();
        var source = new TestSource
        {
            Download = (release, _, _) =>
            {
                calls.Add("download:" + release.Version);
                return Task.FromResult("installer.exe");
            },
        };
        var installer = new TestInstaller
        {
            Launch = (path, _) =>
            {
                calls.Add("launch:" + path);
                return Task.FromResult(true);
            },
        };
        var updates = new ReleaseUpdateUseCase(new Version(1, 0, 1), source, installer);

        Assert.IsTrue(await updates.InstallAsync(NewerRelease));
        Assert.HasCount(2, calls);
        Assert.AreEqual("download:1.0.2", calls[0]);
        Assert.AreEqual("launch:installer.exe", calls[1]);
    }

    [TestMethod]
    public async Task EqualOrOlderReleaseIsRejectedBeforeDownloadOrLaunch()
    {
        foreach (var version in new[] { new Version(1, 0, 1), new Version(1, 0, 0) })
        {
            int downloads = 0;
            int launches = 0;
            var source = new TestSource
            {
                Download = (_, _, _) =>
                {
                    downloads++;
                    return Task.FromResult("installer.exe");
                },
            };
            var installer = new TestInstaller
            {
                Launch = (_, _) =>
                {
                    launches++;
                    return Task.FromResult(true);
                },
            };
            var release = NewerRelease with { Version = version };
            var updates = new ReleaseUpdateUseCase(new Version(1, 0, 1), source, installer);

            var exception = await Assert.ThrowsExactlyAsync<UpdateException>(
                () => updates.InstallAsync(release));

            Assert.AreEqual("UPD-NOT-NEWER", exception.ErrorCode);
            Assert.AreEqual(0, downloads);
            Assert.AreEqual(0, launches);
        }
    }

    [TestMethod]
    public async Task DownloadFailureIsPropagatedAndDoesNotLaunch()
    {
        var expected = new UpdateException("UPD-DOWNLOAD-HASH");
        int launches = 0;
        var source = new TestSource
        {
            Download = (_, _, _) => Task.FromException<string>(expected),
        };
        var installer = new TestInstaller
        {
            Launch = (_, _) =>
            {
                launches++;
                return Task.FromResult(true);
            },
        };
        var updates = new ReleaseUpdateUseCase(new Version(1, 0, 1), source, installer);

        var exception = await Assert.ThrowsExactlyAsync<UpdateException>(
            () => updates.InstallAsync(NewerRelease));

        Assert.AreSame(expected, exception);
        Assert.AreEqual("UPD-DOWNLOAD-HASH", exception.ErrorCode);
        Assert.AreEqual(0, launches);
    }

    [TestMethod]
    public async Task InstallReturnsInstallerResult()
    {
        var updates = new ReleaseUpdateUseCase(
            new Version(1, 0, 1),
            new TestSource { Download = (_, _, _) => Task.FromResult("installer.exe") },
            new TestInstaller { Launch = (_, _) => Task.FromResult(false) });

        Assert.IsFalse(await updates.InstallAsync(NewerRelease));
    }

    [TestMethod]
    public async Task PreCanceledCheckDoesNotCallSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int sourceCalls = 0;
        var source = new TestSource
        {
            Latest = _ =>
            {
                sourceCalls++;
                return Task.FromResult(NewerRelease);
            },
            Download = (_, _, _) =>
            {
                sourceCalls++;
                return Task.FromResult("installer.exe");
            },
        };
        var updates = new ReleaseUpdateUseCase(new Version(1, 0, 1), source, new TestInstaller());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => updates.CheckAsync(cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => updates.InstallAsync(NewerRelease, cancellationToken: cancellation.Token));
        Assert.AreEqual(0, sourceCalls);
    }

    [TestMethod]
    public async Task CancellationAfterDownloadDoesNotLaunchInstaller()
    {
        using var cancellation = new CancellationTokenSource();
        int launches = 0;
        var source = new TestSource
        {
            Download = (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult("installer.exe");
            },
        };
        var installer = new TestInstaller
        {
            Launch = (_, _) =>
            {
                launches++;
                return Task.FromResult(true);
            },
        };
        var updates = new ReleaseUpdateUseCase(new Version(1, 0, 1), source, installer);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => updates.InstallAsync(NewerRelease, cancellationToken: cancellation.Token));
        Assert.AreEqual(0, launches);
    }

    [TestMethod]
    public async Task PendingCheckRejectsConcurrentCheckAndReleasesGateAfterCompletion()
    {
        var pending = new TaskCompletionSource<ReleaseUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        int checks = 0;
        var source = new TestSource
        {
            Latest = _ =>
            {
                checks++;
                return checks == 1 ? pending.Task : Task.FromResult(NewerRelease);
            },
        };
        var updates = new ReleaseUpdateUseCase(new Version(1, 0, 1), source, new TestInstaller());
        Task<ReleaseUpdate> first = updates.CheckAsync();

        var busy = await Assert.ThrowsExactlyAsync<UpdateException>(() => updates.CheckAsync());
        Assert.AreEqual("UPD-BUSY", busy.ErrorCode);
        Assert.AreEqual(1, checks);

        pending.SetResult(NewerRelease);
        Assert.AreSame(NewerRelease, await first);
        Assert.AreSame(NewerRelease, await updates.CheckAsync());
        Assert.AreEqual(2, checks);
    }

    [TestMethod]
    public async Task FailedCheckReleasesGateForNextCheck()
    {
        int checks = 0;
        var source = new TestSource
        {
            Latest = _ =>
            {
                checks++;
                return checks == 1
                    ? Task.FromException<ReleaseUpdate>(new UpdateException("UPD-CHECK-FAILED"))
                    : Task.FromResult(NewerRelease);
            },
        };
        var updates = new ReleaseUpdateUseCase(new Version(1, 0, 1), source, new TestInstaller());

        await Assert.ThrowsExactlyAsync<UpdateException>(() => updates.CheckAsync());
        Assert.AreSame(NewerRelease, await updates.CheckAsync());
        Assert.AreEqual(2, checks);
    }

    private sealed class TestSource : IReleaseUpdateSource
    {
        internal Func<CancellationToken, Task<ReleaseUpdate>> Latest { get; init; } =
            _ => Task.FromResult(NewerRelease);

        internal Func<ReleaseUpdate, IProgress<int>?, CancellationToken, Task<string>> Download { get; init; } =
            (_, _, _) => Task.FromResult("installer.exe");

        public Task<ReleaseUpdate> GetLatestAsync(CancellationToken cancellationToken = default) =>
            Latest(cancellationToken);

        public Task<string> DownloadAsync(
            ReleaseUpdate release,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) =>
            Download(release, progress, cancellationToken);
    }

    private sealed class TestInstaller : IUpdateInstaller
    {
        internal Func<string, CancellationToken, Task<bool>> Launch { get; init; } =
            (_, _) => Task.FromResult(true);

        public Task<bool> LaunchAsync(string installerPath, CancellationToken cancellationToken = default) =>
            Launch(installerPath, cancellationToken);
    }
}
