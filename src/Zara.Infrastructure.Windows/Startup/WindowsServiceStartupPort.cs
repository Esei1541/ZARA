using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Zara.Application.Startup;
using Zara.Infrastructure.Windows.Continuity;

namespace Zara.Infrastructure.Windows.Startup;

public sealed class WindowsServiceStartupPort : IServiceStartupPort
{
    internal const string WorkerArgument = "--start-zara-service";
    internal const string DesktopExecutableName = "Zara.Desktop.exe";
    private readonly IServiceStartupNative _native;
    private readonly IServiceStartupElevator _elevator;

    public WindowsServiceStartupPort() : this(
        new WindowsServiceStartupNative(Path.Combine(AppContext.BaseDirectory,
            WindowsSupervisionServerVerifier.ServiceExecutableName)),
        new WindowsServiceStartupElevator(Path.Combine(AppContext.BaseDirectory,
            DesktopExecutableName)))
    { }

    internal WindowsServiceStartupPort(IServiceStartupNative native, IServiceStartupElevator elevator)
    {
        _native = native;
        _elevator = elevator;
    }

    public ServiceStartupStatus ReadStatus()
    {
        try { return _native.ReadStatus(); }
        catch (Exception exception) when (exception is not DesktopStartupException)
        { throw ConvertFailure(exception, "The ZARA Service status could not be read."); }
    }

    public async Task StartAsync(bool elevated, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (elevated)
            {
                int code = await _elevator.RunAsync(cancellationToken).ConfigureAwait(false);
                if (code != 0) throw WindowsServiceStartupProcess.DecodeWorkerFailure(code);
            }
            else
            {
                await Task.Run(_native.Start, cancellationToken)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not DesktopStartupException)
        { throw ConvertFailure(exception, "The ZARA Service could not be started."); }
    }

    internal static DesktopStartupException ConvertFailure(Exception exception, string message)
    {
        if (exception is DesktopStartupException startup) return startup;
        if (exception is Win32Exception native)
        {
            StartupFailureKind kind = native.NativeErrorCode switch
            {
                5 => StartupFailureKind.AccessDenied,
                1060 => StartupFailureKind.ServiceMissing,
                1072 => StartupFailureKind.ServiceDeleting,
                1058 => StartupFailureKind.ServiceDisabled,
                2 or 3 => StartupFailureKind.BinaryMissing,
                1223 => StartupFailureKind.ElevationCancelled,
                _ => StartupFailureKind.StartFailed
            };
            return new DesktopStartupException(kind, message, native.NativeErrorCode, exception);
        }
        return new DesktopStartupException(
            exception is InvalidDataException
                ? StartupFailureKind.IdentityMismatch : StartupFailureKind.Unexpected,
            message, innerException: exception);
    }
}

internal interface IServiceStartupNative
{
    ServiceStartupStatus ReadStatus();
    void Start();
}

internal interface IServiceStartupElevator
{
    Task<int> RunAsync(CancellationToken cancellationToken);
}

internal sealed partial class WindowsServiceStartupNative : IServiceStartupNative
{
    internal const uint QueryAccess = 0x0001 | 0x0004;
    internal const uint StartAccess = QueryAccess | 0x0010;
    internal static uint RequestedAccess(bool forStart) =>
        forStart ? StartAccess : QueryAccess;
    private const uint ScManagerConnect = 0x0001;
    private const uint OwnProcess = 0x10;
    private const uint Disabled = 4;
    private const int InsufficientBuffer = 122;
    private readonly string _expectedPath;

    internal WindowsServiceStartupNative(string expectedPath) =>
        _expectedPath = Path.GetFullPath(expectedPath);

    public ServiceStartupStatus ReadStatus() => WithService(RequestedAccess(false),
        service => ValidateAndMap(ReadSnapshot(service)));

    public void Start() => WithService(RequestedAccess(true), service =>
    {
        ServiceStartupStatus status = ValidateAndMap(ReadSnapshot(service));
        if (status.State is StartupServiceState.Running or StartupServiceState.StartPending)
            return 0;
        if (status.StartDisabled)
            throw new DesktopStartupException(StartupFailureKind.ServiceDisabled,
                "The ZARA Service is disabled.");
        if (StartService(service.DangerousGetHandle(), 0, 0) == 0)
        {
            int code = Marshal.GetLastPInvokeError();
            if (!IsConcurrentAlreadyRunning(code))
                throw new Win32Exception(code, "Windows did not start the ZARA Service.");
        }
        return 0;
    });

    private ServiceStartupStatus ValidateAndMap(StartupServiceSnapshot snapshot)
    {
        ValidateSnapshot(snapshot, _expectedPath, File.Exists(_expectedPath));
        return MapSnapshot(snapshot);
    }

    internal static bool IsConcurrentAlreadyRunning(int nativeCode) => nativeCode == 1056;

    internal static ServiceStartupStatus MapSnapshot(StartupServiceSnapshot snapshot) =>
        new(snapshot.State switch
        {
            1 => StartupServiceState.Stopped,
            2 => StartupServiceState.StartPending,
            3 => StartupServiceState.StopPending,
            4 => StartupServiceState.Running,
            7 => StartupServiceState.Paused,
            _ => StartupServiceState.Other
        }, snapshot.Win32ExitCode, snapshot.ServiceExitCode, snapshot.StartType == Disabled);

    internal static void ValidateSnapshot(StartupServiceSnapshot snapshot,
        string expectedPath, bool binaryExists)
    {
        if (snapshot.ServiceType != OwnProcess)
            throw new InvalidDataException("The ZARA Service is not a dedicated process.");
        WindowsSupervisionServerVerifier.ValidateRegisteredServiceConfiguration(
            snapshot.ConfiguredServiceType, snapshot.AccountName,
            snapshot.BinaryPath, expectedPath);
        if (!binaryExists)
            throw new DesktopStartupException(StartupFailureKind.BinaryMissing,
                "The registered ZARA Service executable is missing.");
    }

    private static T WithService<T>(uint access, Func<SafeServiceControlHandle, T> work)
    {
        using var manager = new SafeServiceControlHandle(OpenSCManager(null, null, ScManagerConnect));
        if (manager.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot open Service Control Manager.");
        using var service = new SafeServiceControlHandle(OpenService(
            manager.DangerousGetHandle(), WindowsSupervisionServerVerifier.ServiceName, access));
        if (service.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot open ZARA Service.");
        return work(service);
    }

    private static StartupServiceSnapshot ReadSnapshot(SafeServiceControlHandle service)
    {
        int size = Marshal.SizeOf<ServiceStatusProcess>();
        nint buffer = Marshal.AllocHGlobal(size);
        ServiceStatusProcess status;
        try
        {
            if (QueryServiceStatusEx(service.DangerousGetHandle(), 0, buffer,
                    checked((uint)size), out _) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read ZARA Service status.");
            status = Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }

        if (QueryServiceConfig(service.DangerousGetHandle(), 0, 0, out uint bytes) != 0)
            throw new InvalidDataException("Invalid zero-length ZARA Service configuration.");
        int code = Marshal.GetLastPInvokeError();
        if (code != InsufficientBuffer || bytes == 0)
            throw new Win32Exception(code, "Cannot read ZARA Service configuration size.");
        nint configBuffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (QueryServiceConfig(service.DangerousGetHandle(), configBuffer, bytes, out _) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read ZARA Service configuration.");
            ServiceConfiguration config = Marshal.PtrToStructure<ServiceConfiguration>(configBuffer);
            return new StartupServiceSnapshot(status.CurrentState, status.ServiceType,
                status.Win32ExitCode, status.ServiceSpecificExitCode,
                config.ServiceType, config.StartType,
                Marshal.PtrToStringUni(config.ServiceStartName) ?? string.Empty,
                Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty);
        }
        finally { Marshal.FreeHGlobal(configBuffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        internal uint ServiceType, StartType, ErrorControl;
        internal nint BinaryPathName, LoadOrderGroup;
        internal uint TagId;
        internal nint Dependencies, ServiceStartName, DisplayName;
    }

    private sealed class SafeServiceControlHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeServiceControlHandle(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseServiceHandle(handle) != 0;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint manager, string name, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int QueryServiceStatusEx(nint service, int level,
        nint buffer, uint size, out uint needed);
    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    private static partial int QueryServiceConfig(nint service, nint buffer,
        uint size, out uint needed);
    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    private static partial int StartService(nint service, uint argumentCount, nint arguments);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CloseServiceHandle(nint handle);
}

internal readonly record struct StartupServiceSnapshot(uint State, uint ServiceType,
    uint Win32ExitCode, uint ServiceExitCode, uint ConfiguredServiceType,
    uint StartType, string AccountName, string BinaryPath);

internal sealed class WindowsServiceStartupElevator : IServiceStartupElevator
{
    private readonly string _desktopPath;
    internal WindowsServiceStartupElevator(string desktopPath) =>
        _desktopPath = Path.GetFullPath(desktopPath);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_desktopPath))
            throw new DesktopStartupException(StartupFailureKind.BinaryMissing,
                "The installed ZARA Desktop executable is missing.");
        using Process process = Process.Start(CreateStartInfo(_desktopPath)) ??
            throw new InvalidOperationException("Cannot create elevated ZARA worker.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DesktopStartupException(StartupFailureKind.TimedOut,
                "The elevated ZARA worker did not finish in time.");
        }
        return process.ExitCode;
    }

    internal static ProcessStartInfo CreateStartInfo(string path) => new()
    {
        FileName = path,
        Arguments = WindowsServiceStartupPort.WorkerArgument,
        UseShellExecute = true,
        Verb = "runas",
        WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
        CreateNoWindow = true
    };
}
