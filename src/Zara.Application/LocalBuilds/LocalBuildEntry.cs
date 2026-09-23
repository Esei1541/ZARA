#if LOCAL_BUILD_UPDATES
namespace Zara.Application.LocalBuilds;

/// <summary>A catalog row and any problem preventing installation.</summary>
public sealed record LocalBuildEntry(LocalBuildInfo Build, string? Problem)
{
    public bool CanInstall => Problem is null;
}
#endif
