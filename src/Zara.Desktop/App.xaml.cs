using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;
#if LOCAL_BUILD_UPDATES
using Zara.Application.LocalBuilds;
#endif
using Zara.Application.Continuity;
using Zara.Application.Locking;
using Zara.Application.SystemPower;
using Zara.Application.UsagePolicy;
using Zara.Core.Continuity;
using Zara.Core.Runtime;
using Zara.Desktop.Overlays;
using Zara.Desktop.ViewModels;
#if LOCAL_BUILD_UPDATES
using Zara.Infrastructure.Windows.LocalBuilds;
#endif
using Zara.Infrastructure.Windows;
using Zara.Infrastructure.Windows.Continuity;
using Zara.Infrastructure.Windows.UsagePolicy;
using Zara.Supervision.Contracts;

namespace Zara.Desktop;

/// <summary>
/// Composes the supervised desktop process, tray entry point, overlay runtime, and explicit exit.
/// </summary>
public partial class App : System.Windows.Application, IDisposable, IUsagePolicyLockPort
{

    private NotifyIcon? _trayIcon;
    private Icon? _applicationIcon;
    private WindowsDisplayTopology? _displayTopology;
    private WpfLockOverlayPort? _overlayPort;
    private WindowsLockInputPort? _lockInputPort;
    private LockRuntimeUseCase? _lockRuntime;
    private SystemShutdownUseCase? _systemShutdown;
    private ShutdownCancellationWatchdog? _shutdownCancellationWatchdog;
    private WindowsDesktopRestartSettingsStore? _settingsStore;
    private WindowsUsagePolicySettingsStore? _usagePolicySettingsStore;
    private WindowsSupervisionConnection? _supervisionConnection;
    private RestartContinuityUseCase? _restartContinuity;
    private UsagePolicyRuntime? _usagePolicyRuntime;
    private DispatcherTimer? _usagePolicyRefreshTimer;
    private MainWindowViewModel? _mainWindowViewModel;
#if LOCAL_BUILD_UPDATES
    private ILocalBuildUpdates? _localBuildUpdates;
#endif
    private EmergencyUnlockWindow? _emergencyUnlockWindow;
    private bool _emergencyUnlockRequestInProgress;
    private DesktopRestartSettings _restartSettings = DesktopRestartSettings.Default;
    private bool _lockConditionRequired;
    private bool _exitRequestInProgress;
    private CancellationTokenSource? _trayExitCancellation;
    private bool _systemEventsSubscribed;
    private CancellationTokenSource? _systemShutdownWatchdog;
    private int _usagePolicyRefreshInProgress;
    private int _disposeState;

    internal bool IsShuttingDown { get; private set; }

    private bool SystemShutdownRequestInProgress => _shutdownPresentation.IsPending;

    internal void AttachActivationChannel(WindowsDesktopActivationChannel activationChannel)
    {
        ArgumentNullException.ThrowIfNull(activationChannel);
        activationChannel.StartListening(RequestExternalActivationAsync);
    }

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
            bool cancelled = _startupCancellation?.IsCancellationRequested == true;
            DisposeOwnedResources();
            if (e.Args.Length == 0 && !cancelled)
            {
                StartupFailurePresentation failure = StartupFailurePresentation.FromException(exception);
                _ = System.Windows.MessageBox.Show($"{failure.Message}\n\n{failure.Details}", "ZARA 시작 오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
            window = new MainWindow(
                viewModel
#if LOCAL_BUILD_UPDATES
                , _localBuildUpdates ?? throw new InvalidOperationException(
                    "The local build update use case is not initialized.")
#endif
                );
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

    private async Task RequestExternalActivationAsync()
    {
        // An acknowledgement means initialization and the healthy report actually completed.
        await _startupReady.Task.ConfigureAwait(false);
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException("ZARA is shutting down.");
        }
        await Dispatcher.InvokeAsync(() =>
        {
            if (IsShuttingDown)
            {
                throw new InvalidOperationException("ZARA is shutting down.");
            }
            ShowMainWindow();
        }).Task.ConfigureAwait(false);
    }
    private async Task InitializeAsync(IReadOnlyList<string> arguments)
    {
        string? launchToken = ParseServiceLaunchToken(arguments);

        if (!await EstablishSupervisionAsync(launchToken).ConfigureAwait(true))
        {
            Shutdown();
            return;
        }
        _restartContinuity = new RestartContinuityUseCase(
            _supervisionConnection!,
            _restartSettings.RestartOnExitWhenUnlocked);

        _displayTopology = new WindowsDisplayTopology();
        _overlayPort = new WpfLockOverlayPort(
            Dispatcher,
            _displayTopology,
            new NativeWindowPositioner());
        _lockInputPort = new WindowsLockInputPort(_supervisionConnection!);
        _lockRuntime = new LockRuntimeUseCase(_overlayPort, _lockInputPort);
        _lockRuntime.RecoveryStateChanged += OnLockRecoveryStateChanged;
        _systemShutdown = new SystemShutdownUseCase(
            _lockRuntime,
            new WindowsSystemShutdownPort());
        _shutdownCancellationWatchdog = new ShutdownCancellationWatchdog(_systemShutdown);
        InitializeShutdownNotifications();
        _overlayPort.SetSystemShutdownHandler(RequestSystemShutdownAsync);
        _overlayPort.SetEmergencyUnlockHandler(RequestEmergencyUnlockAsync);
#if DEBUG
        _overlayPort.SetDevelopmentUnlockHandler(RequestDevelopmentUnlockAsync);
#endif

        _overlayPort.ProjectionFaulted += OnOverlayProjectionFaulted;
        _applicationIcon = LoadApplicationIcon();
        _trayIcon = CreateTrayIcon(_applicationIcon);

        bool recoverLock = _supervisionConnection!.Registration.RecoverLockOnStart;
        RestartContinuityLease acknowledgedLease = await _restartContinuity
            .PublishLockConditionAsync(recoverLock)
            .ConfigureAwait(true);
        _lockConditionRequired = recoverLock;

        if (recoverLock)
        {
            await RequireOverlayProjectionAsync().ConfigureAwait(true);
            acknowledgedLease = _restartContinuity.CurrentAcknowledgedLease ?? acknowledgedLease;
        }

        _usagePolicySettingsStore = new WindowsUsagePolicySettingsStore();
        _usagePolicyRuntime = new UsagePolicyRuntime(
            _usagePolicySettingsStore,
            this,
            new WindowsEmergencyPromptCatalog(),
            TimeProvider.System);
        await _usagePolicyRuntime.InitializeAsync().ConfigureAwait(true);
        InitializeLockReminders();
        _usagePolicyRuntime.StateChanged += OnUsagePolicyRuntimeStateChanged;
        UpdateEmergencyUnlockAvailability(_usagePolicyRuntime.CurrentSnapshot);
        acknowledgedLease = _restartContinuity.CurrentAcknowledgedLease ?? acknowledgedLease;

#if LOCAL_BUILD_UPDATES
        _localBuildUpdates = new LocalBuildUpdateUseCase(
            new WindowsLocalBuildStore(),
            new WindowsLocalBuildInstaller());
#endif
        _mainWindowViewModel = new MainWindowViewModel(
            _usagePolicyRuntime,
            _restartSettings.RestartOnExitWhenUnlocked,
            UpdateRestartSettingAsync
#if DEBUG
            , RequestLockAsync,
            RequestDevelopmentUnlockAsync
#endif
            , lockReminderSettings: CurrentLockReminderSettings,
            updateLockReminderSetting: UpdateLockReminderSettingAsync
            );
        SubscribeUsagePolicyNotifications();

        await _supervisionConnection
            .ReportHealthyAsync(acknowledgedLease.Revision)
            .ConfigureAwait(true);

        _startupReady.TrySetResult();
        CompleteStartupPresentation();

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

#if DEBUG
    private async Task RequestLockAsync()
    {
        ThrowIfShuttingDown();
        StopLockReminders();
        RestartContinuityUseCase continuity = GetRestartContinuity();

        _ = await continuity
            .PublishLockConditionAsync(lockRequired: true)
            .ConfigureAwait(true);
        _lockConditionRequired = true;
        await RequireOverlayProjectionAsync().ConfigureAwait(true);
    }

#endif

    /// <inheritdoc />
    public async Task ApplyPolicyLockRequirementAsync(
        bool lockRequired,
        bool lockRequiredAfterRestart,
        CancellationToken cancellationToken = default)
    {
        ThrowIfShuttingDown();
        cancellationToken.ThrowIfCancellationRequested();

        RestartContinuityUseCase continuity = GetRestartContinuity();
        LockRuntimeUseCase runtime = GetLockRuntime();
        if (lockRequiredAfterRestart)
        {
            StopLockReminders();
            _ = await continuity
                .PublishLockConditionAsync(lockRequired: true, cancellationToken)
                .ConfigureAwait(true);
        }

        if (lockRequired)
        {
            _lockConditionRequired = true;
            await RequireOverlayProjectionAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            await runtime.RequestUnlockAsync(cancellationToken).ConfigureAwait(true);
            _ = await continuity
                .PublishOverlayProjectionAsync(OverlayProjectionState.Hidden, cancellationToken)
                .ConfigureAwait(true);
        }
        catch
        {
            await TryPublishOverlayProjectionAsync(OverlayProjectionState.Unknown)
                .ConfigureAwait(true);
            throw;
        }

        if (!lockRequiredAfterRestart)
        {
            _ = await continuity
                .PublishLockConditionAsync(lockRequired: false, cancellationToken)
                .ConfigureAwait(true);
        }

        _lockConditionRequired = false;
    }

    private async Task RequireOverlayProjectionAsync()
    {
        LockRuntimeUseCase runtime = GetLockRuntime();
        RestartContinuityUseCase continuity = GetRestartContinuity();

        try
        {
            await runtime.RequestLockAsync().ConfigureAwait(true);
            _ = await continuity
                .PublishOverlayProjectionAsync(runtime.CurrentState.OverlayProjection)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (runtime.CurrentRecovery.IsRecovering)
        {
            Trace.TraceError("Lock effects are awaiting automatic recovery: {0}", exception);
            await TryPublishOverlayProjectionAsync(OverlayProjectionState.Unknown).ConfigureAwait(true);
        }
        catch
        {
            await TryPublishOverlayProjectionAsync(OverlayProjectionState.Unknown)
                .ConfigureAwait(true);
            throw;
        }
    }

#if DEBUG
    private async Task RequestDevelopmentUnlockAsync()
    {
        ThrowIfShuttingDown();
        _trayExitCancellation?.Cancel();
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

        try
        {
            await GetUsagePolicyRuntime()
                .DisableWeeklyScheduleForDevelopmentAsync()
                .ConfigureAwait(true);
            _mainWindowViewModel?.ResetWeeklyScheduleEdits();
        }
        catch (Exception exception)
        {
            Trace.TraceError("Disabling the development usage restrictions failed: {0}", exception);
            string message = exception is UsagePolicySettingsSavedButApplyFailedException
                ? MainWindowViewModel.SavedButApplyFailedMessage
                : "잠금은 해제했지만 모든 요일의 사용 체크를 해제해 저장하지 못했습니다. 다시 시도하세요.";
            _ = System.Windows.MessageBox.Show(
                MainWindow,
                message,
                "잠금 해제(개발용)",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

#endif

    private async Task RequestEmergencyUnlockAsync()
    {
        ThrowIfShuttingDown();
        if (_emergencyUnlockRequestInProgress)
        {
            return;
        }

        _emergencyUnlockRequestInProgress = true;
        try
        {
            UpdateEmergencyUnlockAvailability(GetUsagePolicyRuntime().CurrentSnapshot);
            await RequestEmergencyUnlockCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _emergencyUnlockRequestInProgress = false;
            if (!IsShuttingDown && _usagePolicyRuntime is { } runtime)
            {
                UpdateEmergencyUnlockAvailability(runtime.CurrentSnapshot);
            }
        }
    }

    private async Task RequestEmergencyUnlockCoreAsync()
    {
        ThrowIfShuttingDown();
        if (_emergencyUnlockWindow is { IsVisible: true } openWindow)
        {
            openWindow.Activate();
            return;
        }

        UsagePolicyRuntime runtime = GetUsagePolicyRuntime();
        EmergencyUnlockStartResult start = await runtime
            .StartEmergencyUnlockAsync()
            .ConfigureAwait(true);
        if (start.StartedImmediately)
        {
            ShowMainWindow();
            return;
        }

        EmergencyUnlockChallenge challenge = start.Challenge ??
            throw new InvalidOperationException("The emergency unlock challenge is missing.");
        WpfLockOverlayPort overlayPort = _overlayPort ??
            throw new InvalidOperationException("The overlay port is not initialized.");
        overlayPort.SetEmergencyUnlockEnabled(isEnabled: false);

        var window = new EmergencyUnlockWindow(
            challenge,
            enteredText => runtime.CompleteEmergencyUnlockAsync(enteredText));
        _emergencyUnlockWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_emergencyUnlockWindow, window))
            {
                _emergencyUnlockWindow = null;
            }

            if (!IsShuttingDown)
            {
                UpdateEmergencyUnlockAvailability(runtime.CurrentSnapshot);
            }
        };

        bool? completed = window.ShowDialog();
        if (completed == true && !IsShuttingDown)
        {
            ShowMainWindow();
        }
    }

    private void SubscribeUsagePolicyNotifications()
    {
        if (_systemEventsSubscribed)
        {
            return;
        }

        _usagePolicyRefreshTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _usagePolicyRefreshTimer.Tick += OnUsagePolicyRefreshTimerTick;
        _usagePolicyRefreshTimer.Start();

        SystemEvents.TimeChanged += OnWindowsTimeChanged;
        SystemEvents.PowerModeChanged += OnWindowsPowerModeChanged;
        _systemEventsSubscribed = true;
    }

    private void OnUsagePolicyRuntimeStateChanged(
        object? sender,
        UsagePolicyRuntimeSnapshot snapshot)
    {
        if (IsShuttingDown)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            UpdateEmergencyUnlockAvailability(snapshot);
            UpdateLockReminders();
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (_usagePolicyRuntime is { } runtime)
                {
                    UpdateEmergencyUnlockAvailability(runtime.CurrentSnapshot);
                }
                UpdateLockReminders();
            }));
    }

    private void UpdateEmergencyUnlockAvailability(UsagePolicyRuntimeSnapshot snapshot)
    {
        if (IsShuttingDown)
        {
            return;
        }

        bool canRequestEmergencyUnlock = snapshot.Evaluation.IsWithinUsageBan &&
            snapshot.Evaluation.LockRequired &&
            snapshot.EmergencyUnlockRemainingCount is not 0 &&
            !_emergencyUnlockRequestInProgress &&
            _emergencyUnlockWindow is not { IsVisible: true };
        _overlayPort?.SetEmergencyUnlockRemainingCount(snapshot.EmergencyUnlockRemainingCount);
        _overlayPort?.SetEmergencyUnlockEnabled(canRequestEmergencyUnlock);
    }

    private void UnsubscribeUsagePolicyNotifications()
    {
        if (_usagePolicyRefreshTimer is not null)
        {
            _usagePolicyRefreshTimer.Stop();
            _usagePolicyRefreshTimer.Tick -= OnUsagePolicyRefreshTimerTick;
            _usagePolicyRefreshTimer = null;
        }

        if (_systemEventsSubscribed)
        {
            SystemEvents.TimeChanged -= OnWindowsTimeChanged;
            SystemEvents.PowerModeChanged -= OnWindowsPowerModeChanged;
            _systemEventsSubscribed = false;
        }
    }

    private async void OnUsagePolicyRefreshTimerTick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _usagePolicyRefreshInProgress, 1) != 0 || IsShuttingDown)
        {
            return;
        }

#pragma warning disable CA1031 // Timer callbacks cannot propagate failures to a caller.
        try
        {
            if (_usagePolicyRuntime is not null)
            {
                await _usagePolicyRuntime.RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("The usage-policy refresh failed: {0}", exception);
        }
        finally
        {
            Interlocked.Exchange(ref _usagePolicyRefreshInProgress, 0);
        }
#pragma warning restore CA1031
    }

    private void OnWindowsTimeChanged(object? sender, EventArgs e) =>
        QueueUsagePolicyOperation(runtime =>
        {
            _lockReminders?.ResetObservation();
            return runtime.RefreshAsync();
        });

    private void OnWindowsPowerModeChanged(object? sender, PowerModeChangedEventArgs e) =>
        QueueUsagePolicyOperation(
            runtime =>
            {
                if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
                {
                    _lockReminders?.ResetObservation();
                }
                return e.Mode == PowerModes.Suspend
                    ? ClearEmergencyUnlockForPowerSuspendAsync(runtime)
                    : runtime.RefreshAsync();
            });

    private async Task ClearEmergencyUnlockForPowerSuspendAsync(UsagePolicyRuntime runtime)
    {
        await runtime.ClearEmergencyUnlockAsync().ConfigureAwait(true);
        _emergencyUnlockWindow?.Close();
    }

    private void QueueUsagePolicyOperation(Func<UsagePolicyRuntime, Task> operation)
    {
        if (IsShuttingDown || _usagePolicyRuntime is null)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(async () =>
            {
#pragma warning disable CA1031 // Windows system events have no failure channel.
                try
                {
                    UsagePolicyRuntime runtime = _usagePolicyRuntime ??
                        throw new InvalidOperationException("The usage-policy runtime is not initialized.");
                    await operation(runtime).ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    Trace.TraceError("The usage-policy Windows event handling failed: {0}", exception);
                }
#pragma warning restore CA1031
            }));
    }

    private async Task UpdateRestartSettingAsync(bool restartOnExitWhenUnlocked)
    {
        await _executionSettingsGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await UpdateRestartSettingCoreAsync(restartOnExitWhenUnlocked).ConfigureAwait(true);
        }
        finally
        {
            _executionSettingsGate.Release();
        }
    }

    private async Task UpdateRestartSettingCoreAsync(bool restartOnExitWhenUnlocked)
    {
        ThrowIfShuttingDown();
        if (_usagePolicyRuntime is not null &&
            !_usagePolicyRuntime.CurrentSnapshot.Evaluation.IsSettingsChangeAllowed)
        {
            throw new UsagePolicySettingsLockedException();
        }

        if (_restartSettings.RestartOnExitWhenUnlocked == restartOnExitWhenUnlocked)
        {
            return;
        }

        WindowsDesktopRestartSettingsStore store = _settingsStore ??
            throw new InvalidOperationException("The restart settings store is not initialized.");
        RestartContinuityUseCase continuity = GetRestartContinuity();
        DesktopRestartSettings previous = _restartSettings;
        DesktopRestartSettings updated = previous with { RestartOnExitWhenUnlocked = restartOnExitWhenUnlocked };

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
        if (_exitRequestInProgress || SystemShutdownRequestInProgress || IsShuttingDown || _sessionEnding)
        {
            return;
        }

        SystemShutdownUseCase shutdown = _systemShutdown ??
            throw new InvalidOperationException("The system shutdown use case is not initialized.");
        WpfLockOverlayPort overlayPort = _overlayPort ??
            throw new InvalidOperationException("The overlay port is not initialized.");

        overlayPort.SetSystemShutdownEnabled(isEnabled: false);
        Guid requestId = Guid.NewGuid();
        _shutdownPresentation.Begin(requestId);
        _shutdownNotifications.BeginRequest(requestId);
        string? failureMessage = null;
        LockIntentSnapshot? failureIntent = null;
        try
        {
            await shutdown.RequestShutdownAsync(requestId).ConfigureAwait(true);
            if (_shutdownPresentation.CanStartWatchdog(requestId))
            {
                StartSystemShutdownWatchdog(requestId);
                await PublishCurrentProjectionBestEffortAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("The system shutdown request failed: {0}", exception);
            failureIntent = GetLockRuntime().CurrentIntent;
            await PublishCurrentProjectionBestEffortAsync().ConfigureAwait(true);
            CompleteShutdownAttempt(requestId);
            failureMessage = "PC를 종료하지 못했습니다.";
        }

        if (failureMessage is not null && failureIntent is not null && !IsShuttingDown &&
            _shutdownPresentation.CanShowResult(requestId, failureIntent, GetLockRuntime().CurrentIntent))
        {
            ShowShutdownResult(failureMessage);
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
#pragma warning disable CA1031 // Watchdog failures are logged; adapter recovery belongs to the runtime.
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
        }
        finally
        {
            if (ReferenceEquals(_systemShutdownWatchdog, cancellation))
            {
                _systemShutdownWatchdog = null;
                cancellation.Dispose();
                if (!IsShuttingDown && _shutdownPresentation.CanStartWatchdog(requestId))
                {
                    // Process survival is not a confirmed cancellation. Keep the request pending
                    // until Windows reports an end-session result, without showing a failure dialog.
                    _overlayPort?.SetSystemShutdownEnabled(isEnabled: false);
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
        if (_exitRequestInProgress || IsShuttingDown || _sessionEnding)
        {
            return;
        }

        bool requiresRecovery = GetUsagePolicyRuntime().CurrentSnapshot.Evaluation.LockRequiredAfterRestart;
        MessageBoxResult result = System.Windows.MessageBox.Show(
            requiresRecovery
                ? "잠금이 필요한 시간에는 긴급 해제 중이어도 프로그램이 다시 실행됩니다. 정말로 종료하시겠습니까?"
                : "프로그램이 종료되면 수면 시간을 감지할 수 없습니다. 정말로 종료하시겠습니까?",
            "ZARA",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _exitRequestInProgress = true;
        using var exitCancellation = new CancellationTokenSource();
        _trayExitCancellation = exitCancellation;
        try
        {
            var exit = new DesktopExitUseCase(
                GetUsagePolicyRuntime(),
                GetLockRuntime(),
                GetRestartContinuity());
            await exit.ExecuteAsync(recoverLock => Dispatcher.InvokeAsync(() =>
            {
                if (recoverLock)
                {
                    exitCancellation.Token.ThrowIfCancellationRequested();
                }

                IsShuttingDown = true;
                CancelSystemShutdownWatchdog();
                Shutdown(recoverLock ? 0 : SupervisionProtocol.ExplicitExitCode);
            }).Task, exitCancellation.Token).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The explicit desktop exit failed: {0}", exception);
            if (!exitCancellation.IsCancellationRequested && _lockConditionRequired)
            {
                await RestoreRequiredOverlayBestEffortAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _trayExitCancellation = null;
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

    private UsagePolicyRuntime GetUsagePolicyRuntime() => _usagePolicyRuntime ??
        throw new InvalidOperationException("The usage-policy runtime is not initialized.");

    private void ThrowIfShuttingDown()
    {
        if (IsShuttingDown || _exitRequestInProgress || _sessionEnding)
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

        DisposeStartupResources();
        CancelSystemShutdownWatchdog();
        UnsubscribeUsagePolicyNotifications();
        StopLockReminders();
        _lockReminders = null;
        DisposeRecoveryNotifications();

        if (_lockRuntime is not null)
        {
            _lockRuntime.RecoveryStateChanged -= OnLockRecoveryStateChanged;
            _lockRuntime.Dispose();
        }

        _lockInputPort?.Dispose();
        _lockInputPort = null;

        if (_emergencyUnlockWindow is not null)
        {
            _emergencyUnlockWindow.Close();
            _emergencyUnlockWindow = null;
        }

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

        if (_usagePolicyRuntime is not null)
        {
            _usagePolicyRuntime.StateChanged -= OnUsagePolicyRuntimeStateChanged;
        }

        _usagePolicyRuntime?.Dispose();
        _usagePolicyRuntime = null;

        _usagePolicySettingsStore?.Dispose();
        _usagePolicySettingsStore = null;

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
        _executionSettingsGate.Dispose();
    }
}
