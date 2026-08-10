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
        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }
}
