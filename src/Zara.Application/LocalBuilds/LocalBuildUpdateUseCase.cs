#if LOCAL_BUILD_UPDATES
namespace Zara.Application.LocalBuilds;

/// <summary>Serializes catalog changes and revalidates a build immediately before installation.</summary>
public sealed class LocalBuildUpdateUseCase(
    ILocalBuildStore store,
    ILocalBuildInstaller installer) : ILocalBuildUpdates
{
    private readonly ILocalBuildStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILocalBuildInstaller _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    private int _operationInProgress;

    public Task<LocalBuildCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _store.LoadAsync(cancellationToken), cancellationToken);

    public Task ChangeDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await _store.ChangeDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<bool> InstallAsync(string buildId, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            string path = await _store.ValidateInstallerAsync(buildId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await _installer.LaunchAsync(path, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException("다른 빌드 작업이 진행 중입니다.");
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
        }
    }
}
#endif
