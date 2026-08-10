using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zara.Enforcement.Service.Windows;

/// <summary>
/// Contains the narrow native boundary used by the Session 0 Service host and exact child launcher.
/// </summary>
internal static partial class NativeMethods
{
    internal const uint InvalidSessionId = 0xFFFFFFFF;
    internal const uint CreateSuspended = 0x00000004;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
    internal const uint Infinite = 0xFFFFFFFF;
    internal const uint ResumeThreadFailure = 0xFFFFFFFF;
    internal const uint ErrorProcessAborted = 1067;

    internal const uint ServiceWin32OwnProcess = 0x00000010;
    internal const uint ServiceStartPending = 0x00000002;
    internal const uint ServiceStopPending = 0x00000003;
    internal const uint ServiceRunning = 0x00000004;
    internal const uint ServiceStopped = 0x00000001;
    internal const uint ServiceAcceptStop = 0x00000001;
    internal const uint ServiceAcceptShutdown = 0x00000004;
    internal const uint ServiceAcceptSessionChange = 0x00000080;
    internal const uint ServiceControlStop = 0x00000001;
    internal const uint ServiceControlShutdown = 0x00000005;
    internal const uint ServiceControlSessionChange = 0x0000000E;
    internal const uint WtsSessionLogoff = 0x00000006;
    internal const uint ErrorFailedServiceControllerConnect = 1063;
    internal const uint ErrorNoToken = 1008;

    [Flags]
    internal enum TokenAccess : uint
    {
        AssignPrimary = 0x0001,
        Duplicate = 0x0002,
        Query = 0x0008,
        AdjustDefault = 0x0080,
        AdjustSessionId = 0x0100,
    }

    internal enum SecurityImpersonationLevel
    {
        Anonymous,
        Identification,
        Impersonation,
        Delegation,
    }

    internal enum TokenType
    {
        Primary = 1,
        Impersonation,
    }

    internal enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        internal uint Size;
        internal nint Reserved;
        internal nint Desktop;
        internal nint Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2Size;
        internal nint Reserved2;
        internal nint StandardInput;
        internal nint StandardOutput;
        internal nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal nint Process;
        internal nint Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimitInformation
    {
        internal BasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceTableEntry
    {
        internal nint ServiceName;
        internal nint ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WtsSessionNotification
    {
        internal uint Size;
        internal uint SessionId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out nint tokenHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "WTSGetActiveConsoleSessionId")]
    internal static partial uint GetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQueryUserToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryUserToken(uint sessionId, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "DuplicateTokenEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(
        SafeKernelHandle existingToken,
        TokenAccess desiredAccess,
        nint tokenAttributes,
        SecurityImpersonationLevel impersonationLevel,
        TokenType tokenType,
        out nint duplicatedToken);

    [LibraryImport("userenv.dll", EntryPoint = "CreateEnvironmentBlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateEnvironmentBlock(
        out nint environment,
        SafeKernelHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", EntryPoint = "DestroyEnvironmentBlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "CreateProcessAsUserW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessAsUser(
        SafeKernelHandle token,
        string applicationName,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        in StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    internal static partial nint CreateJobObject(
        nint jobAttributes,
        nint name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(
        SafeKernelHandle job,
        JobObjectInformationClass informationClass,
        in ExtendedLimitInformation information,
        uint informationLength);

    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(
        SafeKernelHandle job,
        SafeKernelHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint ResumeThread(SafeKernelHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(SafeKernelHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true)]
    internal static unsafe partial int StartServiceCtrlDispatcher(ServiceTableEntry* serviceTable);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "RegisterServiceCtrlHandlerExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint RegisterServiceCtrlHandlerEx(
        string serviceName,
        nint handler,
        nint context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int SetServiceStatus(
        nint serviceStatusHandle,
        in ServiceStatus serviceStatus);
}
