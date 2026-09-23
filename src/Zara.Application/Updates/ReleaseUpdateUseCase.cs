namespace Zara.Application.Updates;

/// <summary>Coordinates release checks, verified downloads, and explicit installer requests.</summary>
public sealed class ReleaseUpdateUseCase(
    Version currentVersion,
    IReleaseUpdateSource source,
    IUpdateInstaller installer) : IReleaseUpdates
{
    private readonly IReleaseUpdateSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly IUpdateInstaller _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    private int _operationInProgress;

    public Version CurrentVersion { get; } = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));

    public Task<ReleaseUpdate> CheckAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _source.GetLatestAsync(cancellationToken), cancellationToken);

    public Task<bool> InstallAsync(
        ReleaseUpdate release,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        return RunAsync(async () =>
        {
            if (release.Version <= CurrentVersion)
            {
                throw new UpdateException("UPD-NOT-NEWER");
            }

            string path = await _source.DownloadAsync(release, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await _installer.LaunchAsync(path, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            throw new UpdateException("UPD-BUSY");
        }

        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
        }
    }
}
