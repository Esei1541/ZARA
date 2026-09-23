using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Zara.Application.Updates;

namespace Zara.Infrastructure.Windows.Updates;

/// <summary>Asks Explorer to start the installer outside ZARA's process lifetime.</summary>
public sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    private readonly Func<ProcessStartInfo, bool> _requestInstaller;

    public WindowsUpdateInstaller() : this(RequestInstaller) { }

    internal WindowsUpdateInstaller(Func<ProcessStartInfo, bool> requestInstaller)
    {
        _requestInstaller = requestInstaller ?? throw new ArgumentNullException(nameof(requestInstaller));
    }

    public Task<bool> LaunchAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var startInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    WorkingDirectory = Path.GetDirectoryName(installerPath),
                    UseShellExecute = true,
                    Verb = "runas",
                };
                if (!_requestInstaller(startInfo))
                {
                    throw new InvalidOperationException("Windows에 설치 실행을 요청하지 못했습니다.");
                }

                completion.SetResult(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.SetCanceled(cancellationToken);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                completion.SetResult(false);
            }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x800704C7))
            {
                completion.SetResult(false);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "ZARA installer request",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static bool RequestInstaller(ProcessStartInfo startInfo)
    {
        ExplorerInstallerLauncher.Request(startInfo.FileName, startInfo.WorkingDirectory!);
        return true;
    }
}
