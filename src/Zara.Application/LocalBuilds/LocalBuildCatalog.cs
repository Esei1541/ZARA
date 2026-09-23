#if LOCAL_BUILD_UPDATES
namespace Zara.Application.LocalBuilds;

/// <summary>The selected collection and the identity of the running installation.</summary>
public sealed record LocalBuildCatalog(
    string BuildsDirectory,
    LocalBuildInfo? CurrentBuild,
    IReadOnlyList<LocalBuildEntry> Builds,
    string? Message);
#endif
