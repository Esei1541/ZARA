#if LOCAL_BUILD_UPDATES
using System.Diagnostics;
using Zara.Application.LocalBuilds;
using Zara.Infrastructure.Windows.Updates;

namespace Zara.Infrastructure.Windows.LocalBuilds;

/// <summary>Hands installation to Explorer so stopping ZARA cannot terminate its installer.</summary>
public sealed class WindowsLocalBuildInstaller : ILocalBuildInstaller
{
    private readonly WindowsUpdateInstaller _installer;

    public WindowsLocalBuildInstaller()
        : this(WindowsUpdateInstaller.RequestInstaller)
    {
    }

    internal WindowsLocalBuildInstaller(Func<ProcessStartInfo, bool> requestInstaller)
    {
        _installer = new WindowsUpdateInstaller(requestInstaller);
    }

    public Task<bool> LaunchAsync(string installerPath, CancellationToken cancellationToken = default) =>
        _installer.LaunchAsync(installerPath, cancellationToken);
}
#endif
