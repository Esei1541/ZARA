using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Zara.Application.Continuity;
using Zara.Application.Locking;
using Zara.Application.SystemPower;
using Zara.Core.Continuity;
using Zara.Core.Runtime;
using Zara.Desktop.Overlays;
using Zara.Desktop.ViewModels;
using Zara.Infrastructure.Windows;
using Zara.Infrastructure.Windows.Continuity;
using Zara.Supervision.Contracts;

namespace Zara.Desktop;

/// <summary>
/// Composes the supervised desktop process, tray entry point, overlay runtime, and explicit exit.
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
    private ShutdownCancellationWatchdog? _shutdownCancellationWatchdog;
    private WindowsDesktopRestartSettingsStore? _settingsStore;
    private WindowsSupervisionConnection? _supervisionConnection;
    private RestartContinuityUseCase? _restartContinuity;
    private MainWindowViewModel? _mainWindowViewModel;
    private DesktopRestartSettings _restartSettings = DesktopRestartSettings.Default;
    private bool _lockConditionRequired;
    private bool _exitRequestInProgress;
    private bool _systemShutdownRequestInProgress;
    private CancellationTokenSource? _systemShutdownWatchdog;
    private int _disposeState;

    internal bool IsShuttingDown { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            await InitializeAsync(e.Args).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("ZARA desktop initialization failed: {0}", exception);
            DisposeOwnedResources();
            Shutdown(exitCode: 1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsShuttingDown = true;
        CancelSystemShutdownWatchdog();
        Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Releases tray, supervision, overlay, topology, and runtime resources owned by the process.
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

    private async Task InitializeAsync(IReadOnlyList<string> arguments)
    {
        string? launchToken = ParseServiceLaunchToken(arguments);

        _settingsStore = new WindowsDesktopRestartSettingsStore();
        _restartSettings = await _settingsStore.LoadAsync().ConfigureAwait(true);

        RestartContinuityDecision initialDecision = RestartContinuityPolicy.Decide(
            lockRequired: false,
            _restartSettings.RestartOnExitWhenUnlocked);
        var initialLease = new SupervisionLease(
            Revision: 0,
            RestartRequiredAfterExit: initialDecision.RestartRequired,
            RecoverLockOnRestart: initialDecision.RecoverLock);
        _supervisionConnection = await WindowsSupervisionConnection
            .ConnectAsync(launchToken, initialLease)
            .ConfigureAwait(true);
        _restartContinuity = new RestartContinuityUseCase(
            _supervisionConnection,
            _restartSettings.RestartOnExitWhenUnlocked);

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
        _shutdownCancellationWatchdog = new ShutdownCancellationWatchdog(_systemShutdown);
        _overlayPort.SetSystemShutdownHandler(RequestSystemShutdownAsync);
        _overlayPort.SetDevelopmentUnlockHandler(RequestDevelopmentUnlockAsync);
        _overlayPort.ProjectionFaulted += OnOverlayProjectionFaulted;

        _mainWindowViewModel = new MainWindowViewModel(
            _restartSettings.RestartOnExitWhenUnlocked,
            UpdateRestartSettingAsync,
            RequestLockAsync);
        _applicationIcon = LoadApplicationIcon();
        _trayIcon = CreateTrayIcon(_applicationIcon);

        bool recoverLock = _supervisionConnection.Registration.RecoverLockOnStart;
        RestartContinuityLease acknowledgedLease = await _restartContinuity
            .PublishLockConditionAsync(recoverLock)
            .ConfigureAwait(true);
        _lockConditionRequired = recoverLock;

        if (recoverLock)
        {
            await RequireOverlayProjectionAsync().ConfigureAwait(true);
            acknowledgedLease = _restartContinuity.CurrentAcknowledgedLease ?? acknowledgedLease;
        }

        await _supervisionConnection
            .ReportHealthyAsync(acknowledgedLease.Revision)
            .ConfigureAwait(true);

        if (launchToken is null)
        {
            ShowMainWindow();
        }
    }

    private static string? ParseServiceLaunchToken(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        if (arguments.Count != 2 ||
            !string.Equals(
                arguments[0],
                SupervisionProtocol.ServiceLaunchSwitch,
                StringComparison.Ordinal) ||
            arguments[1].Length != 64 ||
            !arguments[1].All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The desktop launch arguments are invalid.", nameof(arguments));
        }

        return arguments[1];
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
        var exitItem = new ToolStripMenuItem("종료");
        exitItem.Click += async (_, _) => await RequestExitFromTrayAsync().ConfigureAwait(true);

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

    private async Task RequestLockAsync()
    {
        ThrowIfShuttingDown();
        RestartContinuityUseCase continuity = GetRestartContinuity();

        _ = await continuity
            .PublishLockConditionAsync(lockRequired: true)
            .ConfigureAwait(true);
        _lockConditionRequired = true;
        await RequireOverlayProjectionAsync().ConfigureAwait(true);
    }

    private async Task RequireOverlayProjectionAsync()
    {
        LockRuntimeUseCase runtime = GetLockRuntime();
        RestartContinuityUseCase continuity = GetRestartContinuity();

        try
        {
            await runtime.RequestLockAsync().ConfigureAwait(true);
            _ = await continuity
                .PublishOverlayProjectionAsync(OverlayProjectionState.Visible)
                .ConfigureAwait(true);
        }
        catch
        {
            await TryPublishOverlayProjectionAsync(OverlayProjectionState.Unknown)
                .ConfigureAwait(true);
            throw;
        }
    }

    private async Task RequestDevelopmentUnlockAsync()
    {
        ThrowIfShuttingDown();
        RestartContinuityUseCase continuity = GetRestartContinuity();
        LockRuntimeUseCase runtime = GetLockRuntime();

        _ = await continuity
            .PublishLockConditionAsync(lockRequired: false)
            .ConfigureAwait(true);
        _lockConditionRequired = false;

        try
        {
            await runtime.RequestDevelopmentUnlockAsync().ConfigureAwait(true);
            _ = await continuity
                .PublishOverlayProjectionAsync(OverlayProjectionState.Hidden)
                .ConfigureAwait(true);
        }
        catch
        {
            await TryPublishOverlayProjectionAsync(OverlayProjectionState.Unknown)
                .ConfigureAwait(true);
            throw;
        }

        ShowMainWindow();
    }

    private async Task UpdateRestartSettingAsync(bool restartOnExitWhenUnlocked)
    {
        ThrowIfShuttingDown();
        if (_restartSettings.RestartOnExitWhenUnlocked == restartOnExitWhenUnlocked)
        {
            return;
        }

        WindowsDesktopRestartSettingsStore store = _settingsStore ??
            throw new InvalidOperationException("The restart settings store is not initialized.");
        RestartContinuityUseCase continuity = GetRestartContinuity();
        DesktopRestartSettings previous = _restartSettings;
        var updated = new DesktopRestartSettings(restartOnExitWhenUnlocked);

        if (restartOnExitWhenUnlocked)
        {
            _ = await continuity
                .PublishRestartWhenAvailableAsync(restartWhenAvailable: true)
                .ConfigureAwait(true);
            try
            {
                await store.SaveAsync(updated).ConfigureAwait(true);
            }
            catch
            {
                await TryPublishRestartSettingAsync(previous.RestartOnExitWhenUnlocked)
                    .ConfigureAwait(true);
                throw;
            }
        }
        else
        {
            await store.SaveAsync(updated).ConfigureAwait(true);
            try
            {
                _ = await continuity
                    .PublishRestartWhenAvailableAsync(restartWhenAvailable: false)
                    .ConfigureAwait(true);
            }
            catch
            {
                await TrySaveSettingsAsync(previous).ConfigureAwait(true);
                throw;
            }
        }

        _restartSettings = updated;
    }

    private async Task RequestSystemShutdownAsync()
    {
        if (_exitRequestInProgress || _systemShutdownRequestInProgress || IsShuttingDown)
        {
            return;
        }

        SystemShutdownUseCase shutdown = _systemShutdown ??
            throw new InvalidOperationException("The system shutdown use case is not initialized.");
        WpfLockOverlayPort overlayPort = _overlayPort ??
            throw new InvalidOperationException("The overlay port is not initialized.");

        _systemShutdownRequestInProgress = true;
        overlayPort.SetSystemShutdownEnabled(isEnabled: false);
        bool watchdogStarted = false;
        try
        {
            Guid requestId = Guid.NewGuid();
            await shutdown.RequestShutdownAsync(requestId).ConfigureAwait(true);
            StartSystemShutdownWatchdog(requestId);
            watchdogStarted = true;
            await TryPublishOverlayProjectionAsync(OverlayProjectionState.Hidden)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The system shutdown request failed: {0}", exception);
            await PublishCurrentProjectionBestEffortAsync().ConfigureAwait(true);
            overlayPort.SetSystemShutdownEnabled(isEnabled: true);
            throw;
        }
        finally
        {
            if (!watchdogStarted)
            {
                _systemShutdownRequestInProgress = false;
            }
        }
    }

    private void StartSystemShutdownWatchdog(Guid requestId)
    {
        CancelSystemShutdownWatchdog();
        var cancellation = new CancellationTokenSource();
        _systemShutdownWatchdog = cancellation;
        _ = RunSystemShutdownWatchdogAsync(requestId, cancellation);
    }

    private async Task RunSystemShutdownWatchdogAsync(
        Guid requestId,
        CancellationTokenSource cancellation)
    {
#pragma warning disable CA1031 // A failed watchdog must still re-arm the lock UI best effort.
        try
        {
            ShutdownCancellationWatchdog watchdog = _shutdownCancellationWatchdog ??
                throw new InvalidOperationException(
                    "The system shutdown cancellation watchdog is not initialized.");
            _ = await watchdog
                .RecoverIfStillAliveAsync(requestId, cancellation.Token)
                .ConfigureAwait(true);
            if (IsShuttingDown)
            {
                return;
            }
            await PublishCurrentProjectionBestEffortAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Trace.TraceError("The system shutdown cancellation watchdog failed: {0}", exception);
            if (_lockConditionRequired && !IsShuttingDown)
            {
                await RestoreRequiredOverlayBestEffortAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            if (ReferenceEquals(_systemShutdownWatchdog, cancellation))
            {
                _systemShutdownWatchdog = null;
                cancellation.Dispose();
                if (!IsShuttingDown)
                {
                    _systemShutdownRequestInProgress = false;
                    _overlayPort?.SetSystemShutdownEnabled(isEnabled: true);
                }
            }
        }
#pragma warning restore CA1031
    }

    private void CancelSystemShutdownWatchdog()
    {
        CancellationTokenSource? cancellation = _systemShutdownWatchdog;
        _systemShutdownWatchdog = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task RequestExitFromTrayAsync()
    {
        if (_exitRequestInProgress || _systemShutdownRequestInProgress || IsShuttingDown)
        {
            return;
        }

        MessageBoxResult result = System.Windows.MessageBox.Show(
            "프로그램이 종료되면 수면 시간을 감지할 수 없습니다. 정말로 종료하시겠습니까?",
            "ZARA",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _exitRequestInProgress = true;
        try
        {
            LockRuntimeUseCase runtime = GetLockRuntime();
            RestartContinuityUseCase continuity = GetRestartContinuity();

            await runtime.PrepareForExitAsync().ConfigureAwait(true);
            _ = await continuity
                .PublishOverlayProjectionAsync(OverlayProjectionState.Hidden)
                .ConfigureAwait(true);
            _ = await continuity.ReleaseForExplicitExitAsync().ConfigureAwait(true);
            IsShuttingDown = true;
            CancelSystemShutdownWatchdog();
            Shutdown(SupervisionProtocol.ExplicitExitCode);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The explicit desktop exit failed: {0}", exception);
            if (_lockConditionRequired)
            {
                await RestoreRequiredOverlayBestEffortAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            if (!IsShuttingDown)
            {
                _exitRequestInProgress = false;
            }
        }
    }

    private async void OnOverlayProjectionFaulted(object? sender, OverlayProjectionFaultEventArgs e)
    {
#pragma warning disable CA1031 // Projection faults must preserve the continuity lease and UI loop.
        try
        {
            if (_lockRuntime is not null)
            {
                await _lockRuntime
                    .ReportOverlayProjectionInvalidatedAsync(OverlayVisibility.Visible)
                    .ConfigureAwait(true);
            }

            await TryPublishOverlayProjectionAsync(OverlayProjectionState.Unknown)
                .ConfigureAwait(true);
            Trace.TraceError("The overlay projection became invalid: {0}", e.Exception);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Overlay fault reconciliation failed: {0}", exception);
        }
#pragma warning restore CA1031
    }

    private async Task PublishCurrentProjectionBestEffortAsync()
    {
        RuntimeState state = GetLockRuntime().CurrentState;
        await TryPublishOverlayProjectionAsync(state.OverlayProjection).ConfigureAwait(true);
    }

    private async Task RestoreRequiredOverlayBestEffortAsync()
    {
#pragma warning disable CA1031 // Explicit-exit rollback is best effort and preserves the original failure.
        try
        {
            await RequireOverlayProjectionAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The required overlay could not be restored: {0}", exception);
        }
#pragma warning restore CA1031
    }

    private async Task TryPublishOverlayProjectionAsync(OverlayProjectionState projection)
    {
#pragma warning disable CA1031 // Diagnostic continuity updates must not replace the primary result.
        try
        {
            if (_restartContinuity is not null && !_restartContinuity.IsReleased)
            {
                _ = await _restartContinuity
                    .PublishOverlayProjectionAsync(projection, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("The overlay projection lease update failed: {0}", exception);
        }
#pragma warning restore CA1031
    }

    private async Task TryPublishRestartSettingAsync(bool restartWhenAvailable)
    {
#pragma warning disable CA1031 // Rollback is best effort and preserves the settings write failure.
        try
        {
            _ = await GetRestartContinuity()
                .PublishRestartWhenAvailableAsync(restartWhenAvailable, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The restart-setting lease rollback failed: {0}", exception);
        }
#pragma warning restore CA1031
    }

    private async Task TrySaveSettingsAsync(DesktopRestartSettings settings)
    {
#pragma warning disable CA1031 // Rollback is best effort and preserves the lease failure.
        try
        {
            if (_settingsStore is not null)
            {
                await _settingsStore.SaveAsync(settings, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("The restart-setting file rollback failed: {0}", exception);
        }
#pragma warning restore CA1031
    }

    private LockRuntimeUseCase GetLockRuntime() => _lockRuntime ??
        throw new InvalidOperationException("The lock runtime is not initialized.");

    private RestartContinuityUseCase GetRestartContinuity() => _restartContinuity ??
        throw new InvalidOperationException("Restart continuity is not initialized.");

    private void ThrowIfShuttingDown()
    {
        if (IsShuttingDown || _exitRequestInProgress || _systemShutdownRequestInProgress)
        {
            throw new InvalidOperationException("The desktop process is shutting down.");
        }
    }

    private void DisposeOwnedResources()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        CancelSystemShutdownWatchdog();

        if (_overlayPort is not null)
        {
            _overlayPort.ProjectionFaulted -= OnOverlayProjectionFaulted;
        }

        _trayIcon?.ContextMenuStrip?.Dispose();
        _trayIcon?.Dispose();
        _trayIcon = null;

        _applicationIcon?.Dispose();
        _applicationIcon = null;

        _systemShutdown?.Dispose();
        _systemShutdown = null;
        _shutdownCancellationWatchdog = null;

        _lockRuntime?.Dispose();
        _lockRuntime = null;

        _overlayPort?.Dispose();
        _overlayPort = null;

        _displayTopology?.Dispose();
        _displayTopology = null;

        _restartContinuity?.Dispose();
        _restartContinuity = null;

#pragma warning disable CA1031 // Process teardown must release every remaining owned resource.
        try
        {
            if (_supervisionConnection is not null)
            {
                _supervisionConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("The supervision connection did not close cleanly: {0}", exception);
        }
#pragma warning restore CA1031
        _supervisionConnection = null;

        _settingsStore?.Dispose();
        _settingsStore = null;
    }
}
