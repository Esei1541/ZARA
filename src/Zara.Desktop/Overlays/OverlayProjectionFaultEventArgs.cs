namespace Zara.Desktop.Overlays;

/// <summary>
/// Reports a topology-driven projection failure without choosing an automatic retry policy.
/// </summary>
internal sealed class OverlayProjectionFaultEventArgs : EventArgs
{
    internal OverlayProjectionFaultEventArgs(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    internal Exception Exception { get; }
}
