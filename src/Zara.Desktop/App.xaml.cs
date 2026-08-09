using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Zara.Application.Locking;
using Zara.Application.SystemPower;
using Zara.Core.Runtime;
using Zara.Desktop.Overlays;
using Zara.Desktop.ViewModels;
using Zara.Infrastructure.Windows;

namespace Zara.Desktop;

/// <summary>
/// Composes the desktop process, tray entry point, overlay runtime, and safe application shutdown.
/// </summary>
public partial class App : System.Windows.Application, IDisposable
{
#if ZARA_DEVELOPMENT_SAFETY_CONTROLS
    private const bool ShowDevelopmentSafetyControls = true;
#else
    private const bool ShowDevelopmentSafetyControls = false;
#endif

    private NotifyIcon? _trayIcon;
    private Icon? _applicationIcon;
    private WindowsDisplayTopology? _displayTopology;
    private WpfLockOverlayPort? _overlayPort;
    private LockRuntimeUseCase? _lockRuntime;
    private SystemShutdownUseCase? _systemShutdown;
    private WindowsShutdownCancellationGuard? _shutdownGuard;
    private MainWindowViewModel? _mainWindowViewModel;
    private bool _exitRequestInProgress;
    private bool _systemShutdownRequestInProgress;
    private bool _shutdownAwaitingSessionEnd;
    private Guid _activeSystemShutdownRequestId;

    internal bool IsShuttingDown { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _displayTopology = new WindowsDisplayTopology();
            _overlayPort = new WpfLockOverlayPort(
                Dispatcher,
                _displayTopology,
                new NativeWindowPositioner(),
                ShowDevelopmentSafetyControls);
            _lockRuntime = new LockRuntimeUseCase(_overlayPort);
            _systemShutdown = new SystemShutdownUseCase(
                _lockRuntime,
                new WindowsSystemShutdownPort());
            _overlayPort.SetSystemShutdownHandler(RequestSystemShutdownAsync);
            _overlayPort.SetDevelopmentUnlockHandler(RequestDevelopmentUnlockAsync);
            _overlayPort.ProjectionFaulted += OnOverlayProjectionFaulted;

            _mainWindowViewModel = new MainWindowViewModel(_lockRuntime, RequestExitAsync);
            _applicationIcon = LoadApplicationIcon();
            _trayIcon = CreateTrayIcon(_applicationIcon);
            if (WindowsShutdownGuardProcess.IsRecoveryLaunch(e.Args))
            {
                _ = RestoreLockAfterCancelledShutdownLaunchAsync(e.Args);
            }
            else
            {
                ShowMainWindow();
            }
        }
        catch
        {
            DisposeOwnedResources();
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Releases tray, overlay, topology, and runtime resources owned by the desktop process.
    /// </summary>
    public void Dispose()
    {
        DisposeOwnedResources();
        GC.SuppressFinalize(this);
    }

    internal void ShowMainWindow()
    {
        if (MainWindow is not MainWindow window)
        {
            MainWindowViewModel viewModel = _mainWindowViewModel ??
                throw new InvalidOperationException("The main window view model is not initialized.");
            window = new MainWindow(viewModel);
            MainWindow = window;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private static Icon LoadApplicationIcon()
    {
        var resourceUri = new Uri("Assets/ZaraIcon.ico", UriKind.Relative);
        System.Windows.Resources.StreamResourceInfo resource =
            GetResourceStream(resourceUri) ??
            throw new InvalidOperationException("The embedded ZARA icon could not be loaded.");

        using Stream stream = resource.Stream;
        using var sourceIcon = new Icon(stream);
        return (Icon)sourceIcon.Clone();
    }

    private NotifyIcon CreateTrayIcon(Icon icon)
    {
        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("ZARA 열기");
        openItem.Click += (_, _) => ShowMainWindow();

        var exitItem = new ToolStripMenuItem("종료");
        exitItem.Click += async (_, _) => await RequestExitFromTrayAsync().ConfigureAwait(true);

        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        var trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = icon,
            Text = "ZARA",
            Visible = true,
        };
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        return trayIcon;
    }

    private async Task RequestDevelopmentUnlockAsync()
    {
        LockRuntimeUseCase runtime = _lockRuntime ??
            throw new InvalidOperationException("The lock runtime is not initialized.");

        if (_shutdownGuard is not null)
        {
#pragma warning disable CA1031 // Development unlock must proceed even if the guard channel has ended.
            try
            {
                await _shutdownGuard
                    .SuppressFallbackRecoveryAsync(CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        }

        await runtime.RequestDevelopmentUnlockAsync().ConfigureAwait(true);
        _mainWindowViewModel?.RefreshRuntimeState();
        ShowMainWindow();
    }

    private async Task RequestSystemShutdownAsync()
    {
        if (_exitRequestInProgress ||
            _systemShutdownRequestInProgress ||
            _shutdownAwaitingSessionEnd ||
            IsShuttingDown)
        {
            return;
        }

        SystemShutdownUseCase shutdown = _systemShutdown ??
            throw new InvalidOperationException("The system shutdown use case is not initialized.");
        WpfLockOverlayPort overlayPort = _overlayPort ??
            throw new InvalidOperationException("The overlay port is not initialized.");

        overlayPort.ClearOperationError();
        overlayPort.SetSystemShutdownEnabled(isEnabled: false);
        _systemShutdownRequestInProgress = true;
        WindowsShutdownCancellationGuard? guard = null;
        Guid requestId = Guid.NewGuid();
        _activeSystemShutdownRequestId = requestId;

        try
        {
            _shutdownGuard?.Dispose();
            _shutdownGuard = null;
            guard = await WindowsShutdownCancellationGuard
                .ArmAsync(
                    () => RecoverAfterCancelledShutdownAsync(requestId),
                    restoreLockIfParentExits: true,
                    recoveryFailed => CompleteCancelledShutdownAcknowledgementAsync(
                        requestId,
                        recoveryFailed),
                    exception => RecoverAfterShutdownGuardFaultAsync(requestId, exception))
                .ConfigureAwait(true);
            _shutdownGuard = guard;
            if (_lockRuntime?.CurrentIntent.DesiredLock != LockState.Locked)
            {
                await guard
                    .SuppressFallbackRecoveryAsync(CancellationToken.None)
                    .ConfigureAwait(true);
            }
            _shutdownAwaitingSessionEnd = true;
            await shutdown.RequestShutdownAsync(requestId).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _shutdownAwaitingSessionEnd = false;
            if (_activeSystemShutdownRequestId == requestId)
            {
                _activeSystemShutdownRequestId = Guid.Empty;
            }
            if (guard is not null)
            {
                await guard.DisarmAsync(CancellationToken.None).ConfigureAwait(true);
                if (ReferenceEquals(_shutdownGuard, guard))
                {
                    _shutdownGuard = null;
                }
            }

            string failureContext = GetSystemShutdownFailureContext();
            overlayPort.ReportOperationFailure($"{failureContext} {exception.Message}");
            overlayPort.SetSystemShutdownEnabled(isEnabled: true);
            ReportOperationFailure(failureContext, exception);
            throw;
        }
        finally
        {
            _systemShutdownRequestInProgress = false;
        }
    }

    private Task<ShutdownCancellationRecoveryResult> RecoverAfterCancelledShutdownAsync(
        Guid requestId)
    {
        if (Dispatcher.CheckAccess())
        {
            return RecoverAfterCancelledShutdownOnDispatcherAsync(requestId);
        }

        return Dispatcher
            .InvokeAsync(() => RecoverAfterCancelledShutdownOnDispatcherAsync(requestId))
            .Task
            .Unwrap();
    }

    private async Task<ShutdownCancellationRecoveryResult>
        RecoverAfterCancelledShutdownOnDispatcherAsync(Guid requestId)
    {
        SystemShutdownUseCase shutdown = _systemShutdown ??
            throw new InvalidOperationException("The system shutdown use case is not initialized.");
        WpfLockOverlayPort overlayPort = _overlayPort ??
            throw new InvalidOperationException("The overlay port is not initialized.");

        try
        {
            ShutdownCancellationRecoveryResult result =
                await shutdown
                    .HandleShutdownCancellationAsync(requestId)
                    .ConfigureAwait(true);
            if (_activeSystemShutdownRequestId != requestId)
            {
                return result;
            }

            _mainWindowViewModel?.RefreshRuntimeState();

            if (result == ShutdownCancellationRecoveryResult.LockRestored)
            {
                overlayPort.ReportOperationFailure(
                    "Windows 종료가 취소되어 잠금 화면을 다시 적용했습니다.");
            }
            else if (result == ShutdownCancellationRecoveryResult.SupersededByNewerIntent)
            {
                ShowMainWindow();
            }

            return result;
        }
        catch (Exception exception)
        {
            const string failureContext =
                "Windows 종료가 취소되었지만 잠금 화면을 복구하지 못했습니다.";
            overlayPort.ReportOperationFailure($"{failureContext} {exception.Message}");
            ReportOperationFailure(failureContext, exception);
            throw;
        }
    }

    private async Task CompleteCancelledShutdownAcknowledgementAsync(
        Guid requestId,
        bool recoveryFailed)
    {
        if (recoveryFailed)
        {
            await Dispatcher.InvokeAsync(() => EnsureActiveShutdownRequest(requestId));
            WindowsShutdownCancellationGuard guard = _shutdownGuard ??
                throw new InvalidOperationException("The shutdown guard is not initialized.");
            await guard
                .SuppressFallbackRecoveryAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }

        await Dispatcher
            .InvokeAsync(() => CompleteCancelledShutdownAcknowledgementOnDispatcher(requestId))
            .Task;
    }

    private void EnsureActiveShutdownRequest(Guid requestId)
    {
        if (_activeSystemShutdownRequestId != requestId)
        {
            throw new InvalidOperationException(
                "The shutdown cancellation acknowledgement is stale.");
        }
    }

    private void CompleteCancelledShutdownAcknowledgementOnDispatcher(Guid requestId)
    {
        if (_activeSystemShutdownRequestId != requestId)
        {
            return;
        }

        _shutdownAwaitingSessionEnd = false;
        _activeSystemShutdownRequestId = Guid.Empty;
        _overlayPort?.SetSystemShutdownEnabled(isEnabled: true);
    }

    private Task RecoverAfterShutdownGuardFaultAsync(Guid requestId, Exception exception)
    {
        if (Dispatcher.CheckAccess())
        {
            return RecoverAfterShutdownGuardFaultOnDispatcherAsync(requestId, exception);
        }

        return Dispatcher
            .InvokeAsync(() => RecoverAfterShutdownGuardFaultOnDispatcherAsync(requestId, exception))
            .Task
            .Unwrap();
    }

    private async Task RecoverAfterShutdownGuardFaultOnDispatcherAsync(
        Guid requestId,
        Exception exception)
    {
        SystemShutdownUseCase shutdown = _systemShutdown ??
            throw new InvalidOperationException("The system shutdown use case is not initialized.");

#pragma warning disable CA1031 // A failed first compensation is followed by one explicit projection retry.
        try
        {
            _ = await shutdown
                .HandleShutdownCancellationAsync(requestId)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031

        await EnsureCurrentLockIntentProjectedAsync().ConfigureAwait(true);
        _mainWindowViewModel?.RefreshRuntimeState();
        const string failureContext =
            "Windows 종료 확인 프로세스와의 연결이 끊겨 잠금 상태를 안전하게 복구했습니다.";
        _overlayPort?.ReportOperationFailure($"{failureContext} {exception.Message}");
        ReportOperationFailure(failureContext, exception);
    }

    private async Task EnsureCurrentLockIntentProjectedAsync()
    {
        LockRuntimeUseCase runtime = _lockRuntime ??
            throw new InvalidOperationException("The lock runtime is not initialized.");
        LockIntentSnapshot intent = runtime.CurrentIntent;
        RuntimeState state = runtime.CurrentState;

        if (intent.DesiredLock == LockState.Locked)
        {
            if (state.DesiredLock != LockState.Locked ||
                state.OverlayProjection != OverlayProjectionState.Visible)
            {
                await runtime.RequestLockAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
        else if (state.DesiredLock != LockState.Unlocked ||
                 state.OverlayProjection != OverlayProjectionState.Hidden)
        {
            await runtime
                .RequestDevelopmentUnlockAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }

        RuntimeState projected = runtime.CurrentState;
        bool projectionMatchesIntent = runtime.CurrentIntent.DesiredLock switch
        {
            LockState.Locked =>
                projected.DesiredLock == LockState.Locked &&
                projected.OverlayProjection == OverlayProjectionState.Visible,
            LockState.Unlocked =>
                projected.DesiredLock == LockState.Unlocked &&
                projected.OverlayProjection == OverlayProjectionState.Hidden,
            _ => false,
        };
        if (!projectionMatchesIntent)
        {
            throw new InvalidOperationException(
                "The active lock intent could not be projected after the shutdown guard failed.");
        }
    }

    private async Task RestoreLockAfterCancelledShutdownLaunchAsync(
        IReadOnlyList<string> arguments)
    {
#pragma warning disable CA1031 // Recovery launch must keep a visible diagnostic route on failure.
        try
        {
            await WindowsShutdownGuardProcess
                .CompleteRecoveryLaunchAsync(
                    arguments,
                    () => Dispatcher
                        .InvokeAsync(RestoreLockAfterCancelledShutdownOnDispatcherAsync)
                        .Task
                        .Unwrap())
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ReportOperationFailure(
                "Windows 종료 취소 뒤 잠금 화면을 다시 시작하지 못했습니다.",
                exception);
        }
#pragma warning restore CA1031
    }

    private async Task RestoreLockAfterCancelledShutdownOnDispatcherAsync()
    {
        LockRuntimeUseCase runtime = _lockRuntime ??
            throw new InvalidOperationException("The lock runtime is not initialized.");
        WpfLockOverlayPort overlayPort = _overlayPort ??
            throw new InvalidOperationException("The overlay port is not initialized.");

        await runtime.RequestLockAsync().ConfigureAwait(true);
        _mainWindowViewModel?.RefreshRuntimeState();
        overlayPort.ReportOperationFailure(
            "Windows 종료가 취소되어 잠금 화면을 다시 적용했습니다.");
    }

    private async Task RequestExitAsync()
    {
        if (_exitRequestInProgress ||
            _systemShutdownRequestInProgress ||
            _shutdownAwaitingSessionEnd ||
            IsShuttingDown)
        {
            return;
        }

        LockRuntimeUseCase runtime = _lockRuntime ??
            throw new InvalidOperationException("The lock runtime is not initialized.");

        _exitRequestInProgress = true;
        try
        {
            await runtime.PrepareForExitAsync().ConfigureAwait(true);
            _mainWindowViewModel?.RefreshRuntimeState();
            IsShuttingDown = true;
            Shutdown();
        }
        finally
        {
            if (!IsShuttingDown)
            {
                _exitRequestInProgress = false;
            }
        }
    }

    private async Task RequestExitFromTrayAsync()
    {
        try
        {
            await RequestExitAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ReportOperationFailure("안전하게 종료하지 못했습니다.", exception);
        }
    }

    private async void OnOverlayProjectionFaulted(object? sender, OverlayProjectionFaultEventArgs e)
    {
#pragma warning disable CA1031 // This UI event boundary must not crash before safety unlock remains available.
        try
        {
            if (_lockRuntime is not null)
            {
                await _lockRuntime
                    .ReportOverlayProjectionInvalidatedAsync(OverlayVisibility.Visible)
                    .ConfigureAwait(true);
            }

            ReportOperationFailure(
                "화면 구성이 변경된 뒤 오버레이를 다시 맞추지 못했습니다. 잠금 화면의 복구 수단을 사용하십시오.",
                e.Exception);
        }
        catch (Exception reportingException)
        {
            ReportOperationFailure(
                "오버레이 실패 상태를 기록하지 못했습니다. 잠금 화면의 복구 수단을 사용하십시오.",
                reportingException);
        }
#pragma warning restore CA1031
    }

    private void ReportOperationFailure(string context, Exception exception)
    {
        _mainWindowViewModel?.ReportOperationFailure(context, exception);
        ShowMainWindow();
    }

    private string GetSystemShutdownFailureContext()
    {
        RuntimeState? state = _lockRuntime?.CurrentState;
        return state switch
        {
            { DesiredLock: LockState.Locked, OverlayProjection: OverlayProjectionState.Visible } =>
                "시스템 종료를 요청하지 못했습니다. 잠금 화면은 현재 유지되고 있습니다.",
            { DesiredLock: LockState.Unlocked, OverlayProjection: OverlayProjectionState.Hidden } =>
                "시스템 종료를 요청하지 못했습니다. 현재 잠금은 해제되어 있습니다.",
            _ => "시스템 종료와 잠금 화면 복구 결과를 확인할 수 없습니다.",
        };
    }

    private void DisposeOwnedResources()
    {
        if (_overlayPort is not null)
        {
            _overlayPort.ProjectionFaulted -= OnOverlayProjectionFaulted;
        }

        _trayIcon?.ContextMenuStrip?.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;

        _applicationIcon?.Dispose();
        _applicationIcon = null;

        _shutdownGuard?.Dispose();
        _shutdownGuard = null;

        _systemShutdown?.Dispose();
        _systemShutdown = null;

        _lockRuntime?.Dispose();
        _lockRuntime = null;

        _overlayPort?.Dispose();
        _overlayPort = null;

        _displayTopology?.Dispose();
        _displayTopology = null;
    }
}
