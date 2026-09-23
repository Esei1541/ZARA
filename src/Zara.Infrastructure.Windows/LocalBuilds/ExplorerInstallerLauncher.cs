#if LOCAL_BUILD_UPDATES
using System.Runtime.InteropServices;

namespace Zara.Infrastructure.Windows.LocalBuilds;

/// <summary>Uses the desktop Explorer's automation object, never an in-process ShellExecute fallback.</summary>
internal static class ExplorerInstallerLauncher
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    internal static void Request(string installerPath, string workingDirectory)
    {
        object? windows = null;
        object? desktop = null;
        object? view = null;
        object? shell = null;
        try
        {
            Type type = Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: true)!;
            windows = Activator.CreateInstance(type) ??
                throw new InvalidOperationException("Windows 탐색기에 연결하지 못했습니다.");
            object location = 0; // CSIDL_DESKTOP
            object root = Type.Missing;
            int windowHandle;
            desktop = ((dynamic)windows).FindWindowSW(
                ref location, ref root, 8 /* SWC_DESKTOP */, out windowHandle, 1 /* SWFO_NEEDDISPATCH */);
            if (desktop is null || windowHandle == 0)
            {
                throw new InvalidOperationException("Windows 바탕 화면을 찾지 못했습니다. 탐색기를 다시 시작한 뒤 재시도하십시오.");
            }

            view = ((dynamic)desktop).Document ?? throw new InvalidOperationException("Windows 바탕 화면에 연결하지 못했습니다.");
            shell = ((dynamic)view).Application ?? throw new InvalidOperationException("Windows 탐색기에 설치 실행을 요청할 수 없습니다.");
            // This is the existing Explorer process's IShellDispatch2, outside ZARA's lifetime job.
            // Pass the executable as a separate BSTR; no command shell or interpolated arguments.
            ((dynamic)shell).ShellExecute(installerPath, string.Empty, workingDirectory, "runas", 1);
        }
        finally
        {
            Release(shell);
            Release(view);
            Release(desktop);
            Release(windows);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }
}
#endif
