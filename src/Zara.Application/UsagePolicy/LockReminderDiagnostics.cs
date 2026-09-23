using System.Diagnostics;

namespace Zara.Application.UsagePolicy;

/// <summary>Isolates optional reminder diagnostics from scheduling and playback.</summary>
public sealed class LockReminderDiagnostics(Action<string>? write = null)
{
    /// <summary>Records an event without allowing a diagnostic failure to affect locking.</summary>
    public void Record(string message)
    {
#pragma warning disable CA1031 // Diagnostics are optional and must never interrupt enforcement.
        try
        {
            write?.Invoke(message);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The lock reminder diagnostic could not be recorded: {0}", exception);
        }
#pragma warning restore CA1031
    }
}
