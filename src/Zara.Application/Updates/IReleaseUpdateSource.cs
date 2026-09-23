namespace Zara.Application.Updates;

public interface IReleaseUpdateSource
{
    Task<ReleaseUpdate> GetLatestAsync(CancellationToken cancellationToken = default);

    Task<string> DownloadAsync(
        ReleaseUpdate release,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
