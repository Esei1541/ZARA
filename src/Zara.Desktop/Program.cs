using Zara.Infrastructure.Windows;

namespace Zara.Desktop;

/// <summary>
/// Selects the lightweight shutdown-guard worker before constructing the WPF application.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs either the native shutdown guard or the normal ZARA desktop application.
    /// </summary>
    /// <param name="args">Process arguments supplied by Windows or the internal guard.</param>
    /// <returns>The process exit code.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        if (WindowsShutdownGuardProcess.TryRunWorker(args, out int workerExitCode))
        {
            return workerExitCode;
        }

        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }
}
