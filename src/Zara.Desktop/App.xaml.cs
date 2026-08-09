using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace Zara.Desktop;

public partial class App : System.Windows.Application
{
    private NotifyIcon? _trayIcon;

    internal bool IsShuttingDown { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _trayIcon = CreateTrayIcon();
        ShowMainWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnExit(e);
    }

    internal void ShowMainWindow()
    {
        if (MainWindow is not MainWindow window)
        {
            window = new MainWindow();
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

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("ZARA 열기");
        openItem.Click += (_, _) => ShowMainWindow();

        var exitItem = new ToolStripMenuItem("종료");
        exitItem.Click += (_, _) => RequestExit();

        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        var trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Information,
            Text = "ZARA",
            Visible = true,
        };
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        return trayIcon;
    }

    internal void RequestExit()
    {
        IsShuttingDown = true;
        Shutdown();
    }
}
