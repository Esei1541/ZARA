namespace Zara.Enforcement.Service.Supervision;

/// <summary>
/// Uses the runtime timer queue for supervisor retry delays.
/// </summary>
internal sealed class SystemSupervisionDelay : ISupervisionDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }
}
