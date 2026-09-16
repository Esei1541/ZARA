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
    private static readonly ActiveConsoleGenerationCoordinator GenerationCoordinator = new();
    private static readonly ServiceMainCallback ServiceMainCallbackRoot = ServiceMain;
    private static readonly ServiceControlHandlerCallback ServiceControlHandlerCallbackRoot =
        HandleServiceControl;

    private static nint _statusHandle;
    private static CancellationTokenSource? _serviceStopping;
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
                RunSessionGenerationsAsync(_serviceStopping.Token)
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
            GenerationCoordinator.Reset();
            _statusHandle = nint.Zero;
        }
    }

    private static Task RunSessionGenerationsAsync(CancellationToken cancellationToken) =>
        RunSessionGenerationsAsync(
            TryGetActiveConsoleSessionTarget,
            (delay, token) => Task.Delay(delay, token),
            RunActiveConsoleGenerationAsync,
            cancellationToken);

    internal static async Task RunSessionGenerationsAsync(
        Func<ActiveConsoleSessionTarget?> readTarget,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<
            ActiveConsoleSessionTarget,
            CancellationToken,
            Task<SessionGenerationRunResult>> runGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readTarget);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(runGeneration);

        ActiveConsoleSessionTarget? previouslyEndedTarget = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveConsoleSessionTarget target = await WaitForNextActiveConsoleSessionAsync(
                    readTarget,
                    delay,
                    previouslyEndedTarget,
                    cancellationToken)
                .ConfigureAwait(false);
            SessionGenerationRunResult result = await runGeneration(target, cancellationToken)
                .ConfigureAwait(false);
            if (!result.SessionEnded)
            {
                return;
            }

            previouslyEndedTarget = target;
        }
    }

    private static async Task<SessionGenerationRunResult> RunActiveConsoleGenerationAsync(
        ActiveConsoleSessionTarget target,
        CancellationToken serviceCancellationToken)
    {
        var commandSource = new LatestSupervisionCommandSource(
            CreateInitialSupervisionDirective());
        var generation = new ActiveConsoleGeneration(target, commandSource);
        var handshakeRegistry = new DesktopLaunchHandshakeRegistry();
        var launcher = new WindowsDesktopProcessLauncher(
            target.SessionId,
            handshakeRegistry);
        var supervisor = new DesktopSupervisor(
            launcher,
            commandSource,
            new SystemSupervisionDelay());
        var taskManagerRestriction = TaskManagerRestriction.Create(
            AppContext.BaseDirectory,
            target.UserSid.Value);
        var pipeServer = new WindowsSupervisionPipeServer(
            commandSource,
            handshakeRegistry,
            target.SessionId,
            target.UserSid,
            requireInitialServiceLaunch: true,
            taskManagerRestriction: taskManagerRestriction);

        if (!GenerationCoordinator.TryActivate(generation, target.LifecycleVersion))
        {
            _ = generation.TryEndSession(target.SessionId);
            return new(SessionEnded: true);
        }

        try
        {
            // A logoff can race between selecting the console token and publishing the generation
            // to the SCM callback. Confirming the exact logon token here closes that gap without
            // treating a transient token-query failure as a new login.
            await ConfirmGenerationTargetAsync(generation, serviceCancellationToken)
                .ConfigureAwait(false);

            bool sessionEnded = await RunGenerationComponentsAsync(
                    supervisor.RunAsync,
                    pipeServer.RunAsync,
                    generation.SessionEnded,
                    serviceCancellationToken)
                .ConfigureAwait(false);
            return new(sessionEnded);
        }
        finally
        {
            GenerationCoordinator.Clear(generation);
            taskManagerRestriction.Restore();
        }
    }

    private static async Task ConfirmGenerationTargetAsync(
        ActiveConsoleGeneration generation,
        CancellationToken serviceCancellationToken)
    {
        while (!generation.SessionEnded.IsCompleted)
        {
            serviceCancellationToken.ThrowIfCancellationRequested();
            ActiveConsoleSessionTarget? currentTarget = TryGetActiveConsoleSessionTarget();
            if (currentTarget is not null)
            {
                if (!IsSameTarget(currentTarget.Value, generation.Target))
                {
                    _ = generation.TryEndSession(generation.Target.SessionId);
                }

                return;
            }

            await Task.Delay(ActiveSessionPollInterval, serviceCancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static async Task<bool> RunGenerationComponentsAsync(
        Func<CancellationToken, Task> runSupervision,
        Func<CancellationToken, Task> runTransport,
        Task sessionEnded,
        CancellationToken serviceCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runSupervision);
        ArgumentNullException.ThrowIfNull(runTransport);
        ArgumentNullException.ThrowIfNull(sessionEnded);

        if (sessionEnded.IsCompletedSuccessfully)
        {
            serviceCancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        using var componentCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            serviceCancellationToken);
        Task supervision = InvokeComponent(runSupervision, componentCancellation.Token);
        Task transport = InvokeComponent(runTransport, componentCancellation.Token);
        Task completed = await Task.WhenAny(supervision, transport, sessionEnded)
            .ConfigureAwait(false);

        if (sessionEnded.IsCompletedSuccessfully)
        {
            componentCancellation.Cancel();

#pragma warning disable CA1031 // Session replacement expects component cancellation after cleanup.
            try
            {
                await Task.WhenAll(supervision, transport).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!supervision.IsFaulted &&
                      !transport.IsFaulted &&
                      !serviceCancellationToken.IsCancellationRequested)
            {
            }
#pragma warning restore CA1031

            serviceCancellationToken.ThrowIfCancellationRequested();
            return true;
        }

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
        return false;
    }

    private static Task InvokeComponent(
        Func<CancellationToken, Task> runComponent,
        CancellationToken cancellationToken)
    {
        try
        {
            return runComponent(cancellationToken) ??
                Task.FromException(
                    new InvalidOperationException("A Service component returned a null task."));
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
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
                    when eventType is NativeMethods.WtsSessionLogon or
                        NativeMethods.WtsSessionLogoff:
                    ObserveConsoleSessionChange(eventType, eventData);
                    break;
            }
        }
        catch
        {
            // No managed exception may cross the unmanaged Service control callback boundary.
        }

        return 0;
    }

    private static void ObserveConsoleSessionChange(uint eventType, nint eventData)
    {
        if (eventData == nint.Zero)
        {
            return;
        }

        NativeMethods.WtsSessionNotification notification =
            Marshal.PtrToStructure<NativeMethods.WtsSessionNotification>(eventData);
        if (notification.SessionId > int.MaxValue)
        {
            return;
        }

        GenerationCoordinator.ObserveSessionChange(
            (int)notification.SessionId,
            isLoggedOn: eventType == NativeMethods.WtsSessionLogon);
    }

    /// <summary>
    /// Builds the first supervision instruction after the Service selects an interactive console
    /// generation. The Service starts the Desktop only; the Desktop later evaluates all product
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
        CancellationToken cancellationToken) =>
        await WaitForNextActiveConsoleSessionAsync(
                readTarget,
                delay,
                previouslyEndedTarget: null,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Waits for a console logon with a different session or Windows authentication identifier.
    /// A transient token-query failure never authorizes the previously ended generation again.
    /// </summary>
    internal static async Task<ActiveConsoleSessionTarget> WaitForNextActiveConsoleSessionAsync(
        Func<ActiveConsoleSessionTarget?> readTarget,
        Func<TimeSpan, CancellationToken, Task> delay,
        ActiveConsoleSessionTarget? previouslyEndedTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readTarget);
        ArgumentNullException.ThrowIfNull(delay);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveConsoleSessionTarget? target = readTarget();
            if (target is not null &&
                (previouslyEndedTarget is null ||
                 !IsSameTarget(target.Value, previouslyEndedTarget.Value)))
            {
                return target.Value;
            }

            await delay(ActiveSessionPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsSameTarget(
        ActiveConsoleSessionTarget left,
        ActiveConsoleSessionTarget right) =>
        left.SessionId == right.SessionId &&
        left.UserSid.Equals(right.UserSid) &&
        left.AuthenticationId == right.AuthenticationId;

    private static ActiveConsoleSessionTarget? TryGetActiveConsoleSessionTarget()
    {
        uint sessionId = NativeMethods.GetActiveConsoleSessionId();
        if (sessionId == NativeMethods.InvalidSessionId || sessionId > int.MaxValue)
        {
            return null;
        }

        SessionLifecycleSnapshot? lifecycle = GenerationCoordinator.TryCapture(
            (int)sessionId);
        if (lifecycle is null ||
            !NativeMethods.QueryUserToken(sessionId, out nint tokenValue))
        {
            return null;
        }

        using var token = new SafeKernelHandle(tokenValue, ownsHandle: true);
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        SecurityIdentifier userSid = identity.User ??
            throw new InvalidOperationException(
                "The active console session token did not contain a user SID.");
        uint statisticsSize = checked((uint)Marshal.SizeOf<NativeMethods.TokenStatistics>());
        if (!NativeMethods.GetTokenInformation(
                token,
                NativeMethods.TokenInformationClass.TokenStatistics,
                out NativeMethods.TokenStatistics statistics,
                statisticsSize,
                out _))
        {
            return null;
        }

        var authenticationId = new LogonSessionId(
            statistics.AuthenticationId.LowPart,
            statistics.AuthenticationId.HighPart);
        if (!GenerationCoordinator.IsCurrent(lifecycle.Value))
        {
            return null;
        }

        return new ActiveConsoleSessionTarget(
            (int)sessionId,
            userSid,
            authenticationId,
            lifecycle.Value.Version);
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
    SecurityIdentifier UserSid,
    LogonSessionId AuthenticationId,
    long LifecycleVersion = 0);

/// <summary>Identifies one Windows logon token generation independently of session-ID reuse.</summary>
internal readonly record struct LogonSessionId(uint LowPart, int HighPart);

/// <summary>
/// Coordinates SCM session notifications with selection and publication of one active generation.
/// A lifecycle version prevents a logoff delivered between token lookup and generation activation
/// from being lost.
/// </summary>
internal sealed class ActiveConsoleGenerationCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<int, SessionLifecycleState> _sessionStates = [];
    private ActiveConsoleGeneration? _activeGeneration;
    private long _nextVersion;

    public SessionLifecycleSnapshot? TryCapture(int sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);

        lock (_gate)
        {
            if (_sessionStates.TryGetValue(sessionId, out SessionLifecycleState state))
            {
                return state.IsLoggedOn
                    ? new SessionLifecycleSnapshot(sessionId, state.Version)
                    : null;
            }

            return new SessionLifecycleSnapshot(sessionId, Version: 0);
        }
    }

    public bool IsCurrent(SessionLifecycleSnapshot snapshot)
    {
        lock (_gate)
        {
            return IsCurrentUnsafe(snapshot.SessionId, snapshot.Version);
        }
    }

    public bool TryActivate(ActiveConsoleGeneration generation, long lifecycleVersion)
    {
        ArgumentNullException.ThrowIfNull(generation);

        lock (_gate)
        {
            if (_activeGeneration is not null)
            {
                throw new InvalidOperationException(
                    "A previous console generation is still active.");
            }

            if (!IsCurrentUnsafe(generation.Target.SessionId, lifecycleVersion))
            {
                return false;
            }

            _activeGeneration = generation;
            return true;
        }
    }

    public void ObserveSessionChange(int sessionId, bool isLoggedOn)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);
        ActiveConsoleGeneration? generationToEnd = null;

        lock (_gate)
        {
            long version = checked(++_nextVersion);
            _sessionStates[sessionId] = new SessionLifecycleState(version, isLoggedOn);
            if (!isLoggedOn && _activeGeneration?.Target.SessionId == sessionId)
            {
                generationToEnd = _activeGeneration;
            }
        }

        _ = generationToEnd?.TryEndSession(sessionId);
    }

    public void Clear(ActiveConsoleGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);

        lock (_gate)
        {
            if (ReferenceEquals(_activeGeneration, generation))
            {
                _activeGeneration = null;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _activeGeneration = null;
            _sessionStates.Clear();
            _nextVersion = 0;
        }
    }

    private bool IsCurrentUnsafe(int sessionId, long lifecycleVersion)
    {
        if (_sessionStates.TryGetValue(sessionId, out SessionLifecycleState state))
        {
            return state.IsLoggedOn && state.Version == lifecycleVersion;
        }

        return lifecycleVersion == 0;
    }

    private readonly record struct SessionLifecycleState(long Version, bool IsLoggedOn);
}

/// <summary>Captures the most recent SCM lifecycle event for one console session.</summary>
internal readonly record struct SessionLifecycleSnapshot(int SessionId, long Version);

/// <summary>
/// Owns the end signal and command state for one exact interactive logon generation.
/// </summary>
internal sealed class ActiveConsoleGeneration
{
    private readonly TaskCompletionSource _sessionEnded = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public ActiveConsoleGeneration(
        ActiveConsoleSessionTarget target,
        LatestSupervisionCommandSource commandSource)
    {
        ArgumentNullException.ThrowIfNull(commandSource);

        Target = target;
        CommandSource = commandSource;
    }

    public ActiveConsoleSessionTarget Target { get; }

    public LatestSupervisionCommandSource CommandSource { get; }

    public Task SessionEnded => _sessionEnded.Task;

    /// <summary>
    /// Ends this generation only when the SCM notification belongs to its exact session.
    /// The no-restart command is published before the host starts cancelling components.
    /// </summary>
    public bool TryEndSession(int sessionId)
    {
        if (sessionId != Target.SessionId)
        {
            return false;
        }

        CommandSource.EndSession();
        _sessionEnded.TrySetResult();
        return true;
    }
}

/// <summary>Describes why one active-console generation stopped running.</summary>
internal readonly record struct SessionGenerationRunResult(
    bool SessionEnded);
