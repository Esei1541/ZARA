#if LOCAL_BUILD_UPDATES
namespace Zara.Application.LocalBuilds;

/// <summary>Lists local builds and hands a selected build to the existing installer.</summary>
public interface ILocalBuildUpdates
{
    Task<LocalBuildCatalog> LoadAsync(CancellationToken cancellationToken = default);

    Task ChangeDirectoryAsync(string directory, CancellationToken cancellationToken = default);

    /// <returns>True when Windows accepted the launch request; false when it reported cancellation.</returns>
    Task<bool> InstallAsync(string buildId, CancellationToken cancellationToken = default);
}
#endif
