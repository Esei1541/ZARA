#if LOCAL_BUILD_UPDATES
namespace Zara.Application.LocalBuilds;

/// <summary>Requests that Windows launch a validated installer with elevation.</summary>
public interface ILocalBuildInstaller
{
    /// <returns>True when Windows accepted the launch request; false when it reported cancellation.</returns>
    Task<bool> LaunchAsync(string installerPath, CancellationToken cancellationToken = default);
}
#endif
