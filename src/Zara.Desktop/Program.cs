using System.Diagnostics;
using Zara.Infrastructure.Windows;

namespace Zara.Desktop;

/// <summary>
/// Constructs the supervised WPF desktop application.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs the normal ZARA desktop application. Service recovery is authenticated after startup.
    /// </summary>
    /// <param name="args">Process arguments supplied by Windows or the internal guard.</param>
    /// <returns>The process exit code.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            using WindowsDesktopActivationChannel? activationChannel = args.Length == 0
                ? WindowsDesktopActivationChannel
                    .AcquireOrActivateAsync()
                    .GetAwaiter()
                    .GetResult()
                : WindowsDesktopActivationChannel.AcquirePrimary();
            if (activationChannel is null)
            {
                return 0;
            }

            var application = new App();
            application.InitializeComponent();
            application.AttachActivationChannel(activationChannel);
            return application.Run();
        }
        catch (Exception exception)
        {
            Trace.TraceError("ZARA desktop process startup failed: {0}", exception);
            return 1;
        }
    }
}
