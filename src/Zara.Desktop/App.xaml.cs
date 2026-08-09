using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Zara.Application.Locking;
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
    private NotifyIcon? _trayIcon;
    private Icon? _applicationIcon;
    private WindowsDisplayTopology? _displayTopology;
    private WpfLockOverlayPort? _overlayPort;
    private LockRuntimeUseCase? _lockRuntime;
    private MainWindowViewModel? _mainWindowViewModel;
    private bool _exitRequestInProgress;

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
                new NativeWindowPositioner());
            _lockRuntime = new LockRuntimeUseCase(_overlayPort);
            _overlayPort.SetDevelopmentUnlockHandler(RequestDevelopmentUnlockAsync);
            _overlayPort.ProjectionFaulted += OnOverlayProjectionFaulted;

            _mainWindowViewModel = new MainWindowViewModel(_lockRuntime, RequestExitAsync);
            _applicationIcon = LoadApplicationIcon();
            _trayIcon = CreateTrayIcon(_applicationIcon);
            ShowMainWindow();
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

        await runtime.RequestDevelopmentUnlockAsync().ConfigureAwait(true);
        _mainWindowViewModel?.RefreshRuntimeState();
        ShowMainWindow();
    }

    private async Task RequestExitAsync()
    {
        if (_exitRequestInProgress || IsShuttingDown)
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
                "화면 구성이 변경된 뒤 오버레이를 다시 맞추지 못했습니다. 개발 즉시 해제를 사용하십시오.",
                e.Exception);
        }
        catch (Exception reportingException)
        {
            ReportOperationFailure(
                "오버레이 실패 상태를 기록하지 못했습니다. 개발 즉시 해제를 사용하십시오.",
                reportingException);
        }
#pragma warning restore CA1031
    }

    private void ReportOperationFailure(string context, Exception exception)
    {
        _mainWindowViewModel?.ReportOperationFailure(context, exception);
        ShowMainWindow();
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

        _lockRuntime?.Dispose();
        _lockRuntime = null;

        _overlayPort?.Dispose();
        _overlayPort = null;

        _displayTopology?.Dispose();
        _displayTopology = null;
    }
}
