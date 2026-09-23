namespace Zara.Application.Updates;

public interface IReleaseUpdates
{
    Version CurrentVersion { get; }

    Task<ReleaseUpdate> CheckAsync(CancellationToken cancellationToken = default);

    Task<bool> InstallAsync(
        ReleaseUpdate release,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
