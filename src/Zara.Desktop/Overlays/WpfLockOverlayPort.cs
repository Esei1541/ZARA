using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Zara.Application.Locking;
using Zara.Infrastructure.Windows;

namespace Zara.Desktop.Overlays;

/// <summary>
/// Owns one WPF overlay per attached display while the Application layer requests visibility.
/// </summary>
internal sealed class WpfLockOverlayPort : ILockOverlayPort, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly WindowsDisplayTopology _displayTopology;
    private readonly INativeWindowPositioner _windowPositioner;
    private readonly bool _showDevelopmentSafetyControls;
    private readonly Dictionary<string, OverlayWindow> _windows =
        new(StringComparer.OrdinalIgnoreCase);
    private Func<Task>? _requestSystemShutdown;
    private Func<Task>? _requestDevelopmentUnlock;
    private string? _pendingOperationError;
    private bool _maintainVisibleProjection;
    private bool _topologySubscribed;
    private bool _disposed;
    private int _reconcilePending;

    internal WpfLockOverlayPort(
        Dispatcher dispatcher,
        WindowsDisplayTopology displayTopology,
        INativeWindowPositioner windowPositioner,
        bool showDevelopmentSafetyControls)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _displayTopology = displayTopology ?? throw new ArgumentNullException(nameof(displayTopology));
        _windowPositioner = windowPositioner ?? throw new ArgumentNullException(nameof(windowPositioner));
        _showDevelopmentSafetyControls = showDevelopmentSafetyControls;
    }

    internal event EventHandler<OverlayProjectionFaultEventArgs>? ProjectionFaulted;

    internal void SetSystemShutdownHandler(Func<Task> requestSystemShutdown)
    {
        ArgumentNullException.ThrowIfNull(requestSystemShutdown);

        if (_requestSystemShutdown is not null)
        {
            throw new InvalidOperationException("The system shutdown handler is already configured.");
        }

        _requestSystemShutdown = requestSystemShutdown;
    }

    internal void SetDevelopmentUnlockHandler(Func<Task> requestDevelopmentUnlock)
    {
        ArgumentNullException.ThrowIfNull(requestDevelopmentUnlock);

        if (_requestDevelopmentUnlock is not null)
        {
            throw new InvalidOperationException("The development unlock handler is already configured.");
        }

        _requestDevelopmentUnlock = requestDevelopmentUnlock;
    }

    internal void ReportOperationFailure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ThrowIfDisposed();

        _pendingOperationError = message;
        foreach (OverlayWindow window in _windows.Values)
        {
            window.ShowOperationError(message);
        }
    }

    internal void ClearOperationError()
    {
        ThrowIfDisposed();

        _pendingOperationError = null;
        foreach (OverlayWindow window in _windows.Values)
        {
            window.ClearOperationError();
        }
    }

    internal void SetSystemShutdownEnabled(bool isEnabled)
    {
        ThrowIfDisposed();

        foreach (OverlayWindow window in _windows.Values)
        {
            window.SetSystemShutdownEnabled(isEnabled);
        }
    }

    public Task ShowAllAsync(CancellationToken cancellationToken) =>
        InvokeOnDispatcherAsync(ShowAllCore, cancellationToken);

    public Task HideAllAsync(CancellationToken cancellationToken) =>
        InvokeOnDispatcherAsync(HideAllCore, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            DisposeCore();
        }
        else
        {
            _dispatcher.Invoke(DisposeCore);
        }
    }

    private void ShowAllCore()
    {
        ThrowIfDisposed();

        if (!_maintainVisibleProjection)
        {
            _maintainVisibleProjection = true;
            SubscribeTopology();
        }

        try
        {
            ReconcileCore();
        }
        catch (Exception projectionException)
        {
            _maintainVisibleProjection = false;
            UnsubscribeTopology();

#pragma warning disable CA1031 // Preserve both projection and best-effort cleanup failures.
            try
            {
                CloseAllWindowsCore();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Overlay projection and cleanup both failed.",
                    projectionException,
                    cleanupException);
            }
#pragma warning restore CA1031

            throw;
        }
    }

    private void HideAllCore()
    {
        ThrowIfDisposed();
        _maintainVisibleProjection = false;
        UnsubscribeTopology();
        CloseAllWindowsCore();
    }

    private void ReconcileCore()
    {
        if (!_maintainVisibleProjection)
        {
            return;
        }

        var currentDisplays = _displayTopology
            .Capture()
            .ToDictionary(snapshot => snapshot.DeviceName, StringComparer.OrdinalIgnoreCase);

        foreach ((string deviceName, DisplaySnapshot display) in currentDisplays)
        {
            if (!_windows.TryGetValue(deviceName, out OverlayWindow? window))
            {
                window = CreateOverlayWindow(deviceName);
                _windows.Add(deviceName, window);
            }

            PositionWindow(window, display.PixelBounds);
        }

        string[] removedDevices = _windows.Keys
            .Where(deviceName => !currentDisplays.ContainsKey(deviceName))
            .ToArray();

        foreach (string deviceName in removedDevices)
        {
            OverlayWindow window = _windows[deviceName];
            window.CloseFromCoordinator();

            if (_windows.TryGetValue(deviceName, out OverlayWindow? trackedWindow) &&
                ReferenceEquals(trackedWindow, window))
            {
                _windows.Remove(deviceName);
            }
        }

        if (_windows.Count != currentDisplays.Count)
        {
            throw new InvalidOperationException(
                "The overlay projection does not match the current display topology.");
        }
    }

    private OverlayWindow CreateOverlayWindow(string deviceName)
    {
        var window = new OverlayWindow(
            RequestSystemShutdownAsync,
            RequestDevelopmentUnlockAsync,
            _showDevelopmentSafetyControls);
        if (_pendingOperationError is not null)
        {
            window.ShowOperationError(_pendingOperationError);
        }

        window.DpiChanged += (_, _) => ScheduleReconcile();
        window.Loaded += (_, _) => ScheduleReconcile();
        window.ContentRendered += (_, _) => ScheduleReconcile();
        window.LocationChanged += (_, _) => ScheduleReconcile();
        window.SizeChanged += (_, _) => ScheduleReconcile();
        window.Closed += (_, _) => OnOverlayClosed(deviceName, window);
        return window;
    }

    private void PositionWindow(OverlayWindow window, PixelBounds pixelBounds)
    {
        nint handle = new WindowInteropHelper(window).EnsureHandle();

        if (!window.IsVisible)
        {
            _windowPositioner.PositionTopmostNoActivate(handle, pixelBounds);
            window.Show();
            _windowPositioner.PositionTopmostNoActivate(handle, pixelBounds);
            return;
        }

        _windowPositioner.PositionTopmostNoActivate(handle, pixelBounds);
    }

    private Task RequestDevelopmentUnlockAsync()
    {
        Func<Task> handler = _requestDevelopmentUnlock ??
            throw new InvalidOperationException("The development unlock handler is not configured.");
        return handler();
    }

    private Task RequestSystemShutdownAsync()
    {
        Func<Task> handler = _requestSystemShutdown ??
            throw new InvalidOperationException("The system shutdown handler is not configured.");
        return handler();
    }

    private void OnOverlayClosed(string deviceName, OverlayWindow closedWindow)
    {
        if (_windows.TryGetValue(deviceName, out OverlayWindow? currentWindow) &&
            ReferenceEquals(currentWindow, closedWindow))
        {
            _windows.Remove(deviceName);
            ScheduleReconcile();
        }
    }

    private void SubscribeTopology()
    {
        if (_topologySubscribed)
        {
            return;
        }

        _displayTopology.TopologyChanged += OnTopologyChanged;
        _topologySubscribed = true;
    }

    private void UnsubscribeTopology()
    {
        if (!_topologySubscribed)
        {
            return;
        }

        _displayTopology.TopologyChanged -= OnTopologyChanged;
        _topologySubscribed = false;
    }

    private void OnTopologyChanged(object? sender, EventArgs e) =>
        ScheduleReconcile();

    private void ScheduleReconcile()
    {
        if (Interlocked.Exchange(ref _reconcilePending, 1) != 0)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(ProcessScheduledReconcile));
    }

    private void ProcessScheduledReconcile()
    {
        Interlocked.Exchange(ref _reconcilePending, 0);

        if (_disposed || !_maintainVisibleProjection)
        {
            return;
        }

#pragma warning disable CA1031 // A topology callback cannot propagate to its original event source.
        try
        {
            ReconcileCore();
        }
        catch (Exception exception)
        {
            ProjectionFaulted?.Invoke(this, new OverlayProjectionFaultEventArgs(exception));
        }
#pragma warning restore CA1031
    }

    private void CloseAllWindowsCore()
    {
        List<Exception>? failures = null;
        KeyValuePair<string, OverlayWindow>[] windows = _windows.ToArray();

        foreach ((string deviceName, OverlayWindow window) in windows)
        {
#pragma warning disable CA1031 // Every tracked window must get a close attempt before failure returns.
            try
            {
                window.CloseFromCoordinator();

                if (_windows.TryGetValue(deviceName, out OverlayWindow? trackedWindow) &&
                    ReferenceEquals(trackedWindow, window))
                {
                    _windows.Remove(deviceName);
                }
            }
            catch (Exception exception)
            {
                failures ??= [];
                failures.Add(exception);
            }
#pragma warning restore CA1031
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more overlay windows could not be closed.", failures);
        }
    }

    private Task InvokeOnDispatcherAsync(Action action, CancellationToken cancellationToken)
    {
        if (_dispatcher.CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(
                action,
                DispatcherPriority.Send,
                cancellationToken)
            .Task;
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _maintainVisibleProjection = false;
        UnsubscribeTopology();
        CloseAllWindowsCore();
        _requestSystemShutdown = null;
        _requestDevelopmentUnlock = null;
        _pendingOperationError = null;
        ProjectionFaulted = null;
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
