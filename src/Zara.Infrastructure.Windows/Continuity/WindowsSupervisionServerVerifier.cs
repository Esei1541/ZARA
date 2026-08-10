using System.ComponentModel;
using System.Runtime.InteropServices;
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
    uint ServiceType,
    uint ConfiguredServiceType,
    string ServiceAccountName,
    string ServiceBinaryPath);

/// <summary>
/// Authenticates the local named-pipe server against SCM and the exact registered Service.
/// </summary>
internal sealed partial class WindowsSupervisionServerVerifier : ISupervisionServerVerifier
{
    internal const string ServiceName = "ZARA.Enforcement";
    internal const string ServiceExecutableName = "Zara.Enforcement.Service.exe";

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;
    private const string LocalSystemAccountName = "LocalSystem";

    /// <summary>
    /// The complete Service access mask used by the desktop verifier. Both rights are query-only
    /// and are available to a standard user under the Service's default SCM security descriptor.
    /// </summary>
    internal static readonly uint RequiredServiceAccess =
        ServiceQueryConfig | ServiceQueryStatus;

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

        RegisteredServiceIdentity service = QueryRegisteredServiceIdentity();
        var identity = new SupervisionServerIdentity(
            pipeServerProcessId,
            service.ProcessId,
            service.State,
            service.ServiceType,
            service.ConfiguredServiceType,
            service.AccountName,
            service.BinaryPath);

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

        if (identity.ServiceType != ServiceWin32OwnProcess ||
            identity.ConfiguredServiceType != ServiceWin32OwnProcess)
        {
            throw new InvalidDataException(
                "The registered ZARA Service was not a non-interactive dedicated process.");
        }

        if (!string.Equals(
                identity.ServiceAccountName,
                LocalSystemAccountName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The registered ZARA Service account was not LocalSystem.");
        }

        string configuredPath = GetExactConfiguredExecutablePath(identity.ServiceBinaryPath);
        if (!string.Equals(
                configuredPath,
                Path.GetFullPath(expectedServicePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The registered ZARA Service executable path did not match ZARA Service.");
        }
    }

    private static string GetExactConfiguredExecutablePath(string binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            throw new InvalidDataException(
                "The registered ZARA Service executable path was empty.");
        }

        string candidate = binaryPath.Trim();
        if (candidate[0] == '"')
        {
            if (candidate.Length < 2 || candidate[^1] != '"')
            {
                throw new InvalidDataException(
                    "The registered ZARA Service binary path was not an exact executable path.");
            }

            candidate = candidate[1..^1];
        }

        if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('"'))
        {
            throw new InvalidDataException(
                "The registered ZARA Service binary path was not an exact executable path.");
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                "The registered ZARA Service executable path was invalid.",
                exception);
        }
    }

    private static RegisteredServiceIdentity QueryRegisteredServiceIdentity()
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
                RequiredServiceAccess);
            var service = new SafeServiceControlHandle(serviceValue, ownsHandle: true);
            if (service.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                service.Dispose();
                throw new Win32Exception(
                    error,
                    "Windows did not open the registered ZARA Service for identity queries.");
            }

            using (service)
            {
                ServiceStatusProcess status = QueryServiceProcessStatus(service);
                (string binaryPath, string accountName, uint serviceType) =
                    QueryServiceConfiguration(service);
                return new RegisteredServiceIdentity(
                    status.ProcessId,
                    status.CurrentState,
                    status.ServiceType,
                    serviceType,
                    accountName,
                    binaryPath);
            }
        }
    }

    private static ServiceStatusProcess QueryServiceProcessStatus(
        SafeServiceControlHandle service)
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

    private static (string BinaryPath, string AccountName, uint ServiceType)
        QueryServiceConfiguration(
        SafeServiceControlHandle service)
    {
        if (QueryServiceConfigNative(
                service.DangerousGetHandle(),
                configuration: 0,
                bufferSize: 0,
                out uint bytesNeeded) != 0)
        {
            throw new InvalidDataException(
                "Windows returned an invalid zero-length ZARA Service configuration.");
        }

        int error = Marshal.GetLastPInvokeError();
        if (error != ErrorInsufficientBuffer || bytesNeeded == 0)
        {
            throw new Win32Exception(
                error,
                "Windows did not provide the required ZARA Service configuration size.");
        }

        nint configurationBuffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (QueryServiceConfigNative(
                    service.DangerousGetHandle(),
                    configurationBuffer,
                    bytesNeeded,
                    out _) == 0)
            {
                throw CreateLastWin32Exception(
                    "Windows did not provide the registered ZARA Service configuration.");
            }

            ServiceConfiguration configuration =
                Marshal.PtrToStructure<ServiceConfiguration>(configurationBuffer);
            string binaryPath = Marshal.PtrToStringUni(configuration.BinaryPathName) ??
                throw new InvalidDataException(
                    "The registered ZARA Service executable path was empty.");
            string accountName = Marshal.PtrToStringUni(configuration.ServiceStartName) ??
                throw new InvalidDataException(
                    "The registered ZARA Service account was empty.");
            return (binaryPath, accountName, configuration.ServiceType);
        }
        finally
        {
            Marshal.FreeHGlobal(configurationBuffer);
        }
    }

    private static Win32Exception CreateLastWin32Exception(string message)
    {
        return new Win32Exception(Marshal.GetLastPInvokeError(), message);
    }

    private readonly record struct RegisteredServiceIdentity(
        uint ProcessId,
        uint State,
        uint ServiceType,
        uint ConfiguredServiceType,
        string AccountName,
        string BinaryPath);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        internal uint ServiceType;
        internal uint StartType;
        internal uint ErrorControl;
        internal nint BinaryPathName;
        internal nint LoadOrderGroup;
        internal uint TagId;
        internal nint Dependencies;
        internal nint ServiceStartName;
        internal nint DisplayName;
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

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "QueryServiceConfigW",
        SetLastError = true)]
    private static partial int QueryServiceConfigNative(
        nint service,
        nint configuration,
        uint bufferSize,
        out uint bytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CloseServiceHandle(nint serviceControlHandle);
}
