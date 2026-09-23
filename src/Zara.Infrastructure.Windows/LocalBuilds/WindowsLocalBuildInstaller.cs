#if LOCAL_BUILD_UPDATES
using System.ComponentModel;
using System.Diagnostics;
using Zara.Application.LocalBuilds;

namespace Zara.Infrastructure.Windows.LocalBuilds;

/// <summary>Uses the existing elevated installer; service and file replacement remain its responsibility.</summary>
public sealed class WindowsLocalBuildInstaller : ILocalBuildInstaller
{
    private readonly Func<ProcessStartInfo, bool> _startInstaller;

    public WindowsLocalBuildInstaller()
        : this(StartInstaller)
    {
    }

    internal WindowsLocalBuildInstaller(Func<ProcessStartInfo, bool> startInstaller)
    {
        _startInstaller = startInstaller ?? throw new ArgumentNullException(nameof(startInstaller));
    }

    public Task<bool> LaunchAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                WorkingDirectory = Path.GetDirectoryName(installerPath),
                UseShellExecute = true,
                Verb = "runas",
            };
            try
            {
                if (!_startInstaller(startInfo))
                {
                    throw new InvalidOperationException("설치 프로그램을 시작하지 못했습니다.");
                }

                return true;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                return false;
            }
        }, cancellationToken);
    }

    private static bool StartInstaller(ProcessStartInfo startInfo)
    {
        using Process? process = Process.Start(startInfo);
        return process is not null;
    }
}
#endif
