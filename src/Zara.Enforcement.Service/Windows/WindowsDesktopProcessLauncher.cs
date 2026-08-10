using System.ComponentModel;
using System.Runtime.InteropServices;
using Zara.Enforcement.Service.Supervision;
using Zara.Supervision.Contracts;

namespace Zara.Enforcement.Service.Windows;

/// <summary>
/// Launches the canonical sibling Zara.Desktop executable into one fixed interactive session.
/// The Service must run as LocalSystem with the WTS and process-creation privileges required by
/// Windows; this class never falls back to a different executable or session.
/// </summary>
internal sealed class WindowsDesktopProcessLauncher : IDesktopProcessLauncher
{
    private const int FileNotFoundError = 2;
    private const int InvalidParameterError = 87;
    private const int InvalidStateError = 5023;
    private const uint FailedChildWaitMilliseconds = 5_000;
    private const string InteractiveDesktop = "winsta0\\default";
    private const string DesktopExecutableName = "Zara.Desktop.exe";

    private readonly int _sessionId;
    private readonly string _desktopPath;
    private readonly string _installDirectory;
    private readonly IDesktopLaunchHandshakeFactory _handshakeFactory;

    public WindowsDesktopProcessLauncher(
        int sessionId,
        IDesktopLaunchHandshakeFactory handshakeFactory,
        string? installDirectory = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);
        ArgumentNullException.ThrowIfNull(handshakeFactory);

        _sessionId = sessionId;
        _handshakeFactory = handshakeFactory;
        _installDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory));
        _desktopPath = Path.GetFullPath(
            Path.Combine(_installDirectory, DesktopExecutableName));

        string? actualParent = Path.GetDirectoryName(_desktopPath);
        if (!string.Equals(actualParent, _installDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The desktop executable must be a canonical sibling of the Service executable.");
        }
    }

    public async ValueTask<DesktopLaunchResult> LaunchAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_desktopPath))
        {
            return DesktopLaunchResult.RetryableFailure(
                FileNotFoundError,
                "DesktopExecutableMissing");
        }

        if (!NativeMethods.QueryUserToken((uint)_sessionId, out nint sessionTokenValue))
        {
            return LastError("WTSQueryUserToken");
        }

        var sessionToken = new SafeKernelHandle(
            sessionTokenValue,
            ownsHandle: true);
        using (sessionToken)
        {
            NativeMethods.TokenAccess tokenAccess =
                NativeMethods.TokenAccess.AssignPrimary |
                NativeMethods.TokenAccess.Duplicate |
                NativeMethods.TokenAccess.Query |
                NativeMethods.TokenAccess.AdjustDefault |
                NativeMethods.TokenAccess.AdjustSessionId;
            if (!NativeMethods.DuplicateTokenEx(
                    sessionToken,
                    tokenAccess,
                    nint.Zero,
                    NativeMethods.SecurityImpersonationLevel.Impersonation,
                    NativeMethods.TokenType.Primary,
                    out nint primaryTokenValue))
            {
                return LastError("DuplicateTokenEx");
            }

            var primaryToken = new SafeKernelHandle(
                primaryTokenValue,
                ownsHandle: true);
            using (primaryToken)
            {
                if (!NativeMethods.CreateEnvironmentBlock(
                        out nint environment,
                        primaryToken,
                        inherit: false))
                {
                    return LastError("CreateEnvironmentBlock");
                }

                IDesktopLaunchHandshake? handshake = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    handshake = await _handshakeFactory.CreateAsync(
                        _sessionId,
                        cancellationToken).ConfigureAwait(false);
                    if (!IsSafeOneTimeToken(handshake.OneTimeToken))
                    {
                        return DesktopLaunchResult.RetryableFailure(
                            InvalidParameterError,
                            "InvalidLaunchToken");
                    }

                    DesktopLaunchResult result = CreateSuspendedProcess(
                        primaryToken,
                        environment,
                        handshake);
                    if (result.Succeeded)
                    {
                        handshake = null;
                    }

                    return result;
                }
                finally
                {
                    if (handshake is not null)
                    {
                        await handshake.DisposeAsync().ConfigureAwait(false);
                    }

                    _ = NativeMethods.DestroyEnvironmentBlock(environment);
                }
            }
        }
    }

    private DesktopLaunchResult CreateSuspendedProcess(
        SafeKernelHandle primaryToken,
        nint environment,
        IDesktopLaunchHandshake handshake)
    {
        nint desktopName = Marshal.StringToHGlobalUni(InteractiveDesktop);
        nint commandLine = Marshal.StringToHGlobalUni(
            $"\"{_desktopPath}\" {SupervisionProtocol.RecoverySwitch} " +
            handshake.OneTimeToken);

        try
        {
            var startupInfo = new NativeMethods.StartupInfo
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.StartupInfo>(),
                Desktop = desktopName,
            };
            uint creationFlags =
                NativeMethods.CreateSuspended |
                NativeMethods.CreateUnicodeEnvironment;
            if (!NativeMethods.CreateProcessAsUser(
                    primaryToken,
                    _desktopPath,
                    commandLine,
                    nint.Zero,
                    nint.Zero,
                    inheritHandles: false,
                    creationFlags,
                    environment,
                    _installDirectory,
                    in startupInfo,
                    out NativeMethods.ProcessInformation processInformation))
            {
                return LastError("CreateProcessAsUser");
            }

            return AttachLifetimeAndResume(processInformation, handshake);
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(desktopName);
        }
    }

    private DesktopLaunchResult AttachLifetimeAndResume(
        NativeMethods.ProcessInformation processInformation,
        IDesktopLaunchHandshake handshake)
    {
        using var thread = new SafeKernelHandle(processInformation.Thread, ownsHandle: true);
        var process = new SafeKernelHandle(processInformation.Process, ownsHandle: true);
        SafeKernelHandle? job = null;

        try
        {
            int processId = checked((int)processInformation.ProcessId);
            if (!handshake.TryBindProcess(processId))
            {
                StopFailedChild(process);
                return DesktopLaunchResult.RetryableFailure(
                    InvalidStateError,
                    "BindLaunchTokenToProcess");
            }

            nint jobValue = NativeMethods.CreateJobObject(nint.Zero, nint.Zero);
            job = new SafeKernelHandle(jobValue, ownsHandle: true);
            if (job.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                StopFailedChild(process);
                return DesktopLaunchResult.RetryableFailure(error, "CreateJobObject");
            }

            var limits = new NativeMethods.ExtendedLimitInformation
            {
                BasicLimitInformation = new NativeMethods.BasicLimitInformation
                {
                    LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose,
                },
            };
            if (!NativeMethods.SetInformationJobObject(
                    job,
                    NativeMethods.JobObjectInformationClass.ExtendedLimitInformation,
                    in limits,
                    (uint)Marshal.SizeOf<NativeMethods.ExtendedLimitInformation>()))
            {
                int error = Marshal.GetLastPInvokeError();
                StopFailedChild(process);
                return DesktopLaunchResult.RetryableFailure(
                    error,
                    "SetInformationJobObject");
            }

            if (!NativeMethods.AssignProcessToJobObject(job, process))
            {
                int error = Marshal.GetLastPInvokeError();
                StopFailedChild(process);
                return DesktopLaunchResult.RetryableFailure(
                    error,
                    "AssignProcessToJobObject");
            }

            if (NativeMethods.ResumeThread(thread) == NativeMethods.ResumeThreadFailure)
            {
                int error = Marshal.GetLastPInvokeError();
                StopFailedChild(process);
                return DesktopLaunchResult.RetryableFailure(error, "ResumeThread");
            }

            var supervisedProcess = new WindowsSupervisedProcess(
                processId,
                _sessionId,
                process,
                job,
                handshake);
            process = null!;
            job = null;
            return DesktopLaunchResult.Success(supervisedProcess);
        }
        catch (Win32Exception exception)
        {
            StopFailedChild(process);
            return DesktopLaunchResult.RetryableFailure(
                exception.NativeErrorCode,
                "AttachProcessLifetime");
        }
        finally
        {
            process?.Dispose();
            job?.Dispose();
        }
    }

    private static void StopFailedChild(SafeKernelHandle process)
    {
        _ = NativeMethods.TerminateProcess(process, NativeMethods.ErrorProcessAborted);
        _ = NativeMethods.WaitForSingleObject(process, FailedChildWaitMilliseconds);
    }

    private static DesktopLaunchResult LastError(string failureStage)
    {
        return DesktopLaunchResult.RetryableFailure(
            Marshal.GetLastPInvokeError(),
            failureStage);
    }

    private static bool IsSafeOneTimeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        foreach (char value in token)
        {
            if (!char.IsAsciiHexDigit(value))
            {
                return false;
            }
        }

        return token.Length == 64;
    }
}
