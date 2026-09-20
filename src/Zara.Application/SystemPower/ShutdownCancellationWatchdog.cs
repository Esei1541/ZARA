namespace Zara.Application.SystemPower;

/// <summary>
/// Waits only a short fail-closed grace period after Windows accepts a shutdown request, then asks
/// the shutdown use case to restore the lock when the desktop process is still alive.
/// </summary>
public sealed class ShutdownCancellationWatchdog
{
    /// <summary>
    /// The maximum unlocked interval deliberately allowed without an exact native terminal signal.
    /// </summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(250);

    private readonly ISystemShutdownUseCase _shutdownUseCase;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates the production watchdog using a cancellable system delay.</summary>
    public ShutdownCancellationWatchdog(ISystemShutdownUseCase shutdownUseCase)
        : this(shutdownUseCase, Task.Delay)
    {
    }

    internal ShutdownCancellationWatchdog(
        ISystemShutdownUseCase shutdownUseCase,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(shutdownUseCase);
        ArgumentNullException.ThrowIfNull(delay);
        _shutdownUseCase = shutdownUseCase;
        _delay = delay;
    }

    /// <summary>
    /// Restores the accepted shutdown request when the caller has not cancelled this process
    /// lifetime before the fail-closed grace period expires.
    /// </summary>
    public async Task<ShutdownCancellationRecoveryResult> RecoverIfStillAliveAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException(
                "A nonempty shutdown request identity is required.",
                nameof(requestId));
        }

        await _delay(DefaultDelay, cancellationToken).ConfigureAwait(false);
        return await _shutdownUseCase
            .RestoreLockWhileShutdownPendingAsync(requestId)
            .ConfigureAwait(false);
    }
}
