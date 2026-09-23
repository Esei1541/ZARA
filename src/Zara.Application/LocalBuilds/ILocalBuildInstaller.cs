#if LOCAL_BUILD_UPDATES
namespace Zara.Application.LocalBuilds;

/// <summary>Starts a validated installer with the Windows elevation prompt.</summary>
public interface ILocalBuildInstaller
{
    Task<bool> LaunchAsync(string installerPath, CancellationToken cancellationToken = default);
}
#endif
