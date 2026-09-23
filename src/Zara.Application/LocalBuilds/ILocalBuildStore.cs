#if LOCAL_BUILD_UPDATES
namespace Zara.Application.LocalBuilds;

/// <summary>Reads the collection, stores its location, and validates the selected installer.</summary>
public interface ILocalBuildStore
{
    Task<LocalBuildCatalog> LoadAsync(CancellationToken cancellationToken = default);

    Task ChangeDirectoryAsync(string directory, CancellationToken cancellationToken = default);

    Task<string> ValidateInstallerAsync(string buildId, CancellationToken cancellationToken = default);
}
#endif
