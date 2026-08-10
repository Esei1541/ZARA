using System.Runtime.InteropServices;
using System.Security.Principal;
using Zara.Enforcement.Service.Supervision;
using Zara.Enforcement.Service.Windows;

namespace Zara.Enforcement.Service;

/// <summary>
/// Hosts the supervisor directly under the Windows Service Control Manager without ServiceBase or
/// an additional hosting package. The host stays in Session 0 and creates no windows.
/// </summary>
internal static class WindowsServiceHost
{
    internal const string ServiceName = "ZARA.Enforcement";

    private const uint ServiceStartupWaitHintMilliseconds = 5_000;
    private const uint ServiceStopWaitHintMilliseconds = 10_000;
    private const uint ErrorServiceSpecificError = 1066;
    private static readonly TimeSpan ActiveSessionPollInterval = TimeSpan.FromSeconds(1);

    private static readonly object StatusGate = new();
    private static readonly ServiceMainCallback ServiceMainCallbackRoot = ServiceMain;
    private static readonly ServiceControlHandlerCallback ServiceControlHandlerCallbackRoot =
        HandleServiceControl;

    private static nint _statusHandle;
    private static CancellationTokenSource? _serviceStopping;
    private static LatestSupervisionCommandSource? _commandSource;
    private static DesktopLaunchHandshakeRegistry? _handshakeRegistry;
    private static int _targetSessionId = -1;
    private static uint _checkPoint;

    /// <summary>
    /// Connects the process to SCM. Running the executable outside SCM intentionally fails with
    /// ERROR_FAILED_SERVICE_CONTROLLER_CONNECT rather than starting a privileged console mode.
    /// </summary>
    public static unsafe int Run()
    {
        nint serviceName = Marshal.StringToHGlobalUni(ServiceName);
        try
        {
            var table = stackalloc NativeMethods.ServiceTableEntry[2];
            table[0] = new NativeMethods.ServiceTableEntry
            {
                ServiceName = serviceName,
                ServiceMain = Marshal.GetFunctionPointerForDelegate(ServiceMainCallbackRoot),
            };
            table[1] = default;

            if (NativeMethods.StartServiceCtrlDispatcher(table) != 0)
            {
                return 0;
            }

            return Marshal.GetLastPInvokeError();
        }
        finally
        {
            Marshal.FreeHGlobal(serviceName);
        }
    }

    private static void ServiceMain(uint argumentCount, nint arguments)
    {
        _ = argumentCount;
        _ = arguments;

        try
        {
            _statusHandle = NativeMethods.RegisterServiceCtrlHandlerEx(
                ServiceName,
                Marshal.GetFunctionPointerForDelegate(ServiceControlHandlerCallbackRoot),
                nint.Zero);
            if (_statusHandle == nint.Zero)
            {
                return;
            }

            ReportStatus(
                NativeMethods.ServiceStartPending,
                acceptedControls: 0,
                win32ExitCode: 0,
                serviceSpecificExitCode: 0,
                ServiceStartupWaitHintMilliseconds);

            _serviceStopping = new CancellationTokenSource();
            uint acceptedControls =
                NativeMethods.ServiceAcceptStop |
                NativeMethods.ServiceAcceptShutdown |
                NativeMethods.ServiceAcceptSessionChange;
            ReportStatus(
                NativeMethods.ServiceRunning,
                acceptedControls,
                win32ExitCode: 0,
                serviceSpecificExitCode: 0,
                waitHint: 0);

            uint win32ExitCode = 0;
            uint serviceSpecificExitCode = 0;
            try
            {
                ActiveConsoleSessionTarget target = WaitForActiveConsoleSessionAsync(
                        TryGetActiveConsoleSessionTarget,
                        (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
                        _serviceStopping.Token)
                    .GetAwaiter()
                    .GetResult();
                _targetSessionId = target.SessionId;
                _commandSource = new LatestSupervisionCommandSource(
                    CreateInitialSupervisionDirective());
                _handshakeRegistry = new DesktopLaunchHandshakeRegistry();
                var launcher = new WindowsDesktopProcessLauncher(
                    _targetSessionId,
                    _handshakeRegistry);
                var supervisor = new DesktopSupervisor(
                    launcher,
                    _commandSource,
                    new SystemSupervisionDelay());
                var pipeServer = new WindowsSupervisionPipeServer(
                    _commandSource,
                    _handshakeRegistry,
                    _targetSessionId,
                    target.UserSid,
                    requireInitialServiceLaunch: true);

                RunComponentsAsync(
                        supervisor,
                        pipeServer,
                        _serviceStopping.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException) when (_serviceStopping.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                win32ExitCode = ErrorServiceSpecificError;
                serviceSpecificExitCode = unchecked((uint)exception.HResult);
            }
            finally
            {
                ReportStatus(
                    NativeMethods.ServiceStopped,
                    acceptedControls: 0,
                    win32ExitCode,
                    serviceSpecificExitCode,
                    waitHint: 0);
            }
        }
        catch
        {
            if (_statusHandle != nint.Zero)
            {
                ReportStatus(
                    NativeMethods.ServiceStopped,
                    acceptedControls: 0,
                    ErrorServiceSpecificError,
                    serviceSpecificExitCode: 1,
                    waitHint: 0);
            }
        }
        finally
        {
            _serviceStopping?.Dispose();
            _serviceStopping = null;
            _commandSource = null;
            _handshakeRegistry = null;
            _targetSessionId = -1;
            _statusHandle = nint.Zero;
        }
    }

    private static async Task RunComponentsAsync(
        DesktopSupervisor supervisor,
        WindowsSupervisionPipeServer pipeServer,
        CancellationToken serviceCancellationToken)
    {
        using var componentCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            serviceCancellationToken);
        Task transport = pipeServer.RunAsync(componentCancellation.Token);
        Task supervision = supervisor.RunAsync(componentCancellation.Token);
        Task completed = await Task.WhenAny(supervision, transport).ConfigureAwait(false);

        if (!serviceCancellationToken.IsCancellationRequested)
        {
            componentCancellation.Cancel();
        }

#pragma warning disable CA1031 // Observe both components before preserving the first terminal result.
        try
        {
            await Task.WhenAll(supervision, transport).ConfigureAwait(false);
        }
        catch (Exception) when (serviceCancellationToken.IsCancellationRequested)
        {
        }
#pragma warning restore CA1031

        await completed.ConfigureAwait(false);
    }

    private static uint HandleServiceControl(
        uint control,
        uint eventType,
        nint eventData,
        nint context)
    {
        _ = context;

        try
        {
            switch (control)
            {
                case NativeMethods.ServiceControlStop:
                case NativeMethods.ServiceControlShutdown:
                    ReportStatus(
                        NativeMethods.ServiceStopPending,
                        acceptedControls: 0,
                        win32ExitCode: 0,
                        serviceSpecificExitCode: 0,
                        ServiceStopWaitHintMilliseconds);
                    _serviceStopping?.Cancel();
                    break;

                case NativeMethods.ServiceControlSessionChange
                    when eventType == NativeMethods.WtsSessionLogoff &&
                         IsTargetSession(eventData):
                    _commandSource?.EndSession();
                    break;
            }
        }
        catch
        {
            // No managed exception may cross the unmanaged Service control callback boundary.
        }

        return 0;
    }

    private static bool IsTargetSession(nint eventData)
    {
        if (eventData == nint.Zero || _targetSessionId < 0)
        {
            return false;
        }

        NativeMethods.WtsSessionNotification notification =
            Marshal.PtrToStructure<NativeMethods.WtsSessionNotification>(eventData);
        return notification.SessionId == (uint)_targetSessionId;
    }

    /// <summary>
    /// Builds the first supervision instruction after a boot-time Service finds an interactive
    /// console user. The Service starts the Desktop only; the Desktop later evaluates all product
    /// settings and publishes the resulting restart lease.
    /// </summary>
    internal static SupervisionDirective CreateInitialSupervisionDirective() => new(
        Revision: 0,
        RestartRequired: true,
        SupervisionDirectiveReason.ServiceStarted);

    /// <summary>
    /// Keeps an auto-started Service alive in Session 0 until an interactive console logon has a
    /// queryable user token. The caller starts the first Desktop only after this method returns.
    /// </summary>
    internal static async Task<ActiveConsoleSessionTarget> WaitForActiveConsoleSessionAsync(
        Func<ActiveConsoleSessionTarget?> readTarget,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readTarget);
        ArgumentNullException.ThrowIfNull(delay);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveConsoleSessionTarget? target = readTarget();
            if (target is not null)
            {
                return target.Value;
            }

            await delay(ActiveSessionPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ActiveConsoleSessionTarget? TryGetActiveConsoleSessionTarget()
    {
        uint sessionId = NativeMethods.GetActiveConsoleSessionId();
        if (sessionId == NativeMethods.InvalidSessionId || sessionId > int.MaxValue ||
            !NativeMethods.QueryUserToken(sessionId, out nint tokenValue))
        {
            return null;
        }

        using var token = new SafeKernelHandle(tokenValue, ownsHandle: true);
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        SecurityIdentifier userSid = identity.User ??
            throw new InvalidOperationException(
                "The active console session token did not contain a user SID.");
        return new ActiveConsoleSessionTarget((int)sessionId, userSid);
    }

    private static void ReportStatus(
        uint state,
        uint acceptedControls,
        uint win32ExitCode,
        uint serviceSpecificExitCode,
        uint waitHint)
    {
        lock (StatusGate)
        {
            if (_statusHandle == nint.Zero)
            {
                return;
            }

            bool pending = state is
                NativeMethods.ServiceStartPending or
                NativeMethods.ServiceStopPending;
            var status = new NativeMethods.ServiceStatus
            {
                ServiceType = NativeMethods.ServiceWin32OwnProcess,
                CurrentState = state,
                ControlsAccepted = pending ? 0 : acceptedControls,
                Win32ExitCode = win32ExitCode,
                ServiceSpecificExitCode = serviceSpecificExitCode,
                CheckPoint = pending ? ++_checkPoint : 0,
                WaitHint = waitHint,
            };
            _ = NativeMethods.SetServiceStatus(_statusHandle, in status);

            if (!pending)
            {
                _checkPoint = 0;
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(uint argumentCount, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServiceControlHandlerCallback(
        uint control,
        uint eventType,
        nint eventData,
        nint context);
}

/// <summary>Identifies the one interactive logon generation supervised by this Service run.</summary>
internal readonly record struct ActiveConsoleSessionTarget(
    int SessionId,
    SecurityIdentifier UserSid);
