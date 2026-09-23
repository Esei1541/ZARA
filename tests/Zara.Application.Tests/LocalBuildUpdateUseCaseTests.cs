#if LOCAL_BUILD_UPDATES
using Zara.Application.LocalBuilds;

namespace Zara.Application.Tests;

[TestClass]
public sealed class LocalBuildUpdateUseCaseTests
{
    [TestMethod]
    public async Task InstallationValidatesBeforeHandingOffAndPreservesCancellationResult()
    {
        var calls = new List<string>();
        var store = new TestStore
        {
            Validate = (id, _) =>
            {
                calls.Add("validate:" + id);
                return Task.FromResult(@"C:\builds\older.exe");
            },
        };
        var installer = new TestInstaller
        {
            Launch = (path, _) =>
            {
                calls.Add("launch:" + path);
                return Task.FromResult(false);
            },
        };
        var updates = new LocalBuildUpdateUseCase(store, installer);

        Assert.IsFalse(await updates.InstallAsync("older"));
        Assert.HasCount(2, calls);
        Assert.AreEqual("validate:older", calls[0]);
        Assert.AreEqual(@"launch:C:\builds\older.exe", calls[1]);
    }

    [TestMethod]
    public async Task InvalidBuildNeverStartsInstallerAndNextOperationCanRun()
    {
        int launches = 0;
        var store = new TestStore
        {
            Validate = (_, _) => throw new InvalidDataException("changed file"),
        };
        var updates = new LocalBuildUpdateUseCase(store, new TestInstaller
        {
            Launch = (_, _) =>
            {
                launches++;
                return Task.FromResult(true);
            },
        });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => updates.InstallAsync("missing"));
        Assert.AreEqual(0, launches);
        Assert.IsNotNull(await updates.LoadAsync());
    }

    [TestMethod]
    public async Task PendingInstallRejectsAnotherInstallAndFolderChange()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int validations = 0;
        var store = new TestStore
        {
            Validate = (_, _) =>
            {
                validations++;
                return pending.Task;
            },
        };
        var updates = new LocalBuildUpdateUseCase(store, new TestInstaller());
        Task<bool> first = updates.InstallAsync("first");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => updates.InstallAsync("second"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => updates.ChangeDirectoryAsync("another"));
        Assert.AreEqual(1, validations);
        Assert.AreEqual(0, store.Changes);

        pending.SetResult(@"C:\builds\first.exe");
        Assert.IsTrue(await first);
        await updates.ChangeDirectoryAsync("another");
        Assert.AreEqual(1, store.Changes);
    }

    [TestMethod]
    public async Task CancellationAfterValidationDoesNotStartInstaller()
    {
        using var cancellation = new CancellationTokenSource();
        int launches = 0;
        var store = new TestStore
        {
            Validate = (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(@"C:\builds\build.exe");
            },
        };
        var updates = new LocalBuildUpdateUseCase(store, new TestInstaller
        {
            Launch = (_, _) =>
            {
                launches++;
                return Task.FromResult(true);
            },
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => updates.InstallAsync("build", cancellation.Token));
        Assert.AreEqual(0, launches);
    }

    private sealed class TestStore : ILocalBuildStore
    {
        internal Func<string, CancellationToken, Task<string>> Validate { get; init; } =
            (_, _) => Task.FromResult(@"C:\builds\build.exe");

        internal int Changes { get; private set; }

        public Task<LocalBuildCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalBuildCatalog("builds", null, [], null));

        public Task ChangeDirectoryAsync(string directory, CancellationToken cancellationToken = default)
        {
            Changes++;
            return Task.CompletedTask;
        }

        public Task<string> ValidateInstallerAsync(string buildId, CancellationToken cancellationToken = default) =>
            Validate(buildId, cancellationToken);
    }

    private sealed class TestInstaller : ILocalBuildInstaller
    {
        internal Func<string, CancellationToken, Task<bool>> Launch { get; init; } =
            (_, _) => Task.FromResult(true);

        public Task<bool> LaunchAsync(string installerPath, CancellationToken cancellationToken = default) =>
            Launch(installerPath, cancellationToken);
    }
}
#endif
