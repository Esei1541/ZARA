namespace Zara.Enforcement.Service;

/// <summary>
/// Enters the Service Control Manager dispatcher without creating any interactive UI in Session 0.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs the raw Windows Service host and returns its native startup failure code, if any.
    /// </summary>
    /// <returns>Zero after a normal Service lifetime; otherwise, a Win32 error code.</returns>
    public static int Main()
    {
        return WindowsServiceHost.Run();
    }
}
