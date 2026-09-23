using System.Diagnostics;
using System.Windows;
using Zara.Infrastructure.Windows;
using Zara.Infrastructure.Windows.Startup;

namespace Zara.Desktop;

/// <summary>Constructs the supervised WPF desktop or the restricted service-start worker.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (WindowsServiceStartupProcess.TryRunWorker(args, out int workerExitCode))
            {
                return workerExitCode;
            }
            using WindowsDesktopStartupGate? startupGate = args.Length == 0
                ? WindowsDesktopStartupGate.TryAcquire()
                : null;
            if (args.Length == 0 && startupGate is null)
            {
                return 0;
            }
            var application = new App();
            application.InitializeComponent();
            application.SetStartupGate(startupGate);
            return application.Run();
        }
        catch (Exception exception)
        {
            Trace.TraceError("ZARA desktop process startup failed: {0}", exception);
            if (args.Length == 0)
            {
                StartupFailurePresentation failure = StartupFailurePresentation.FromException(exception);
                _ = System.Windows.MessageBox.Show($"{failure.Message}\n\n{failure.Details}", "ZARA 시작 오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return 1;
        }
    }
}
