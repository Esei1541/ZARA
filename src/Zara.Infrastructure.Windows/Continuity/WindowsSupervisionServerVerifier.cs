using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Zara.Infrastructure.Windows.Continuity;

/// <summary>
/// Verifies that a connected supervision pipe is owned by the exact SCM-managed ZARA Service
/// process before the desktop discloses its identity or restart lease.
/// </summary>
internal interface ISupervisionServerVerifier
{
    /// <summary>
    /// Validates the connected pipe server. Implementations must throw when any trust-boundary
    /// check cannot be completed or does not match the expected Service identity.
    /// </summary>
    void Verify(SafePipeHandle pipeHandle);
}

/// <summary>
/// Captures the independently observed identity fields used to authenticate the pipe server.
/// </summary>
internal readonly record struct SupervisionServerIdentity(
    uint PipeServerProcessId,
    uint ServiceProcessId,
    uint ServiceState,
    uint SessionId,
    string UserSid,
    string ExecutablePath);

/// <summary>
/// Authenticates the local named-pipe server against SCM and the exact protected Service process.
/// </summary>
internal sealed partial class WindowsSupervisionServerVerifier : ISupervisionServerVerifier
{
    internal const string ServiceName = "ZARA.Enforcement";
    internal const string ServiceExecutableName = "Zara.Enforcement.Service.exe";

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 0x00000004;
    private const int MaximumExecutablePathCharacters = 32_768;

    private static readonly string LocalSystemSid = new SecurityIdentifier(
        WellKnownSidType.LocalSystemSid,
        domainSid: null).Value;

    private readonly string _expectedServicePath;

    internal WindowsSupervisionServerVerifier(string expectedServicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServicePath);
        if (!Path.IsPathFullyQualified(expectedServicePath))
        {
            throw new ArgumentException(
                "An absolute ZARA Service executable path is required.",
                nameof(expectedServicePath));
        }

        _expectedServicePath = Path.GetFullPath(expectedServicePath);
    }

    /// <summary>The strict verifier used by every production connection.</summary>
    internal static WindowsSupervisionServerVerifier Default { get; } =
        new WindowsSupervisionServerVerifier(Path.Combine(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            ServiceExecutableName));

    /// <inheritdoc />
    public void Verify(SafePipeHandle pipeHandle)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        if (pipeHandle.IsInvalid || pipeHandle.IsClosed)
        {
            throw new InvalidOperationException(
                "The supervision pipe was not connected for Service verification.");
        }

        if (GetNamedPipeServerProcessId(
                pipeHandle.DangerousGetHandle(),
                out uint pipeServerProcessId) == 0)
        {
            throw CreateLastWin32Exception(
                "Windows did not identify the supervision pipe server process.");
        }

        if (pipeServerProcessId == 0)
        {
            throw new InvalidDataException(
                "Windows returned an invalid supervision pipe server process identifier.");
        }

        using SafeKernelHandle process = OpenRequiredProcess(pipeServerProcessId);
        ServiceStatusProcess serviceStatus = QueryServiceStatus();
        var identity = new SupervisionServerIdentity(
            pipeServerProcessId,
            serviceStatus.ProcessId,
            serviceStatus.CurrentState,
            GetProcessSessionId(pipeServerProcessId),
            GetProcessUserSid(process),
            GetProcessExecutablePath(process));

        ValidateIdentity(identity, _expectedServicePath);
    }

    /// <summary>
    /// Validates an already observed identity snapshot. Kept separate from native discovery so all
    /// rejection branches can be tested without requiring a LocalSystem Service in unit tests.
    /// </summary>
    internal static void ValidateIdentity(
        SupervisionServerIdentity identity,
        string expectedServicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServicePath);
        if (identity.ServiceState != ServiceRunning)
        {
            throw new InvalidDataException("The ZARA Service was not running.");
        }

        if (identity.PipeServerProcessId == 0 ||
            identity.PipeServerProcessId != identity.ServiceProcessId)
        {
            throw new InvalidDataException(
                "The supervision pipe server did not match the SCM Service process.");
        }

        if (identity.SessionId != 0)
        {
            throw new InvalidDataException(
                "The supervision pipe server was not running in Session 0.");
        }

        if (!string.Equals(identity.UserSid, LocalSystemSid, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The supervision pipe server was not running as LocalSystem.");
        }

        if (string.IsNullOrWhiteSpace(identity.ExecutablePath) ||
            !string.Equals(
                Path.GetFullPath(identity.ExecutablePath),
                Path.GetFullPath(expectedServicePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The supervision pipe server executable path did not match ZARA Service.");
        }
    }

    private static SafeKernelHandle OpenRequiredProcess(uint processId)
    {
        nint processValue = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: 0,
            processId);
        var process = new SafeKernelHandle(processValue, ownsHandle: true);
        if (!process.IsInvalid)
        {
            return process;
        }

        int error = Marshal.GetLastPInvokeError();
        process.Dispose();
        throw new Win32Exception(
            error,
            "Windows denied limited identity access to the supervision pipe server.");
    }

    private static uint GetProcessSessionId(uint processId)
    {
        if (ProcessIdToSessionId(processId, out uint sessionId) == 0)
        {
            throw CreateLastWin32Exception(
                "Windows did not provide the supervision pipe server session.");
        }

        return sessionId;
    }

    private static string GetProcessUserSid(SafeKernelHandle process)
    {
        if (OpenProcessToken(
                process.DangerousGetHandle(),
                TokenQuery,
                out nint tokenValue) == 0)
        {
            throw CreateLastWin32Exception(
                "Windows denied token identity access to the supervision pipe server.");
        }

        using var token = new SafeKernelHandle(tokenValue, ownsHandle: true);
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        return identity.User?.Value ??
            throw new InvalidDataException(
                "The supervision pipe server token did not contain a user SID.");
    }

    private static string GetProcessExecutablePath(SafeKernelHandle process)
    {
        nint buffer = Marshal.AllocHGlobal(
            checked(MaximumExecutablePathCharacters * sizeof(char)));
        try
        {
            uint length = MaximumExecutablePathCharacters;
            if (QueryFullProcessImageName(
                    process.DangerousGetHandle(),
                    flags: 0,
                    buffer,
                    ref length) == 0)
            {
                throw CreateLastWin32Exception(
                    "Windows did not provide the supervision pipe server executable path.");
            }

            return Marshal.PtrToStringUni(buffer, checked((int)length)) ??
                throw new InvalidDataException(
                    "The supervision pipe server executable path was empty.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ServiceStatusProcess QueryServiceStatus()
    {
        nint managerValue = OpenSCManager(
            machineName: null,
            databaseName: null,
            ScManagerConnect);
        var manager = new SafeServiceControlHandle(managerValue, ownsHandle: true);
        if (manager.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            manager.Dispose();
            throw new Win32Exception(
                error,
                "Windows denied access to the Service Control Manager.");
        }

        using (manager)
        {
            nint serviceValue = OpenService(
                manager.DangerousGetHandle(),
                ServiceName,
                ServiceQueryStatus);
            var service = new SafeServiceControlHandle(serviceValue, ownsHandle: true);
            if (service.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                service.Dispose();
                throw new Win32Exception(
                    error,
                    "Windows did not open the registered ZARA Service.");
            }

            using (service)
            {
                int statusSize = Marshal.SizeOf<ServiceStatusProcess>();
                nint statusBuffer = Marshal.AllocHGlobal(statusSize);
                try
                {
                    if (QueryServiceStatusEx(
                            service.DangerousGetHandle(),
                            ScStatusProcessInfo,
                            statusBuffer,
                            checked((uint)statusSize),
                            out _) == 0)
                    {
                        throw CreateLastWin32Exception(
                            "Windows did not provide the ZARA Service process status.");
                    }

                    return Marshal.PtrToStructure<ServiceStatusProcess>(statusBuffer);
                }
                finally
                {
                    Marshal.FreeHGlobal(statusBuffer);
                }
            }
        }
    }

    private static Win32Exception CreateLastWin32Exception(string message)
    {
        return new Win32Exception(Marshal.GetLastPInvokeError(), message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
        internal uint ProcessId;
        internal uint ServiceFlags;
    }

    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeKernelHandle(nint handle, bool ownsHandle)
            : base(ownsHandle)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            return CloseHandle(handle) != 0;
        }
    }

    private sealed class SafeServiceControlHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeServiceControlHandle(nint handle, bool ownsHandle)
            : base(ownsHandle)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            return CloseServiceHandle(handle) != 0;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetNamedPipeServerProcessId(
        nint pipe,
        out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(
        uint desiredAccess,
        int inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int ProcessIdToSessionId(
        uint processId,
        out uint sessionId);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        SetLastError = true)]
    private static partial int QueryFullProcessImageName(
        nint process,
        uint flags,
        nint executableName,
        ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenProcessToken(
        nint process,
        uint desiredAccess,
        out nint token);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "OpenSCManagerW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "OpenServiceW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(
        nint serviceControlManager,
        string serviceName,
        uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int QueryServiceStatusEx(
        nint service,
        int informationLevel,
        nint buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CloseServiceHandle(nint serviceControlHandle);
}
