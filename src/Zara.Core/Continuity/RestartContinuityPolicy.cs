namespace Zara.Core.Continuity;

/// <summary>
/// Describes the process-continuity action required after the user-session process exits.
/// </summary>
/// <param name="RestartRequired">
/// <see langword="true"/> when the user-session process must be started again.
/// </param>
/// <param name="RecoverLock">
/// <see langword="true"/> when the restarted process must re-evaluate and recover the lock.
/// </param>
public sealed record RestartContinuityDecision(
    bool RestartRequired,
    bool RecoverLock);

/// <summary>
/// Selects the deterministic restart and lock-recovery action from the current product inputs.
/// </summary>
public static class RestartContinuityPolicy
{
    /// <summary>
    /// Gets the product default used when the normal-time restart setting has not been stored yet.
    /// </summary>
    public const bool DefaultRestartWhenAvailable = true;

    /// <summary>
    /// Determines whether the user-session process and lock must be recovered after process exit.
    /// </summary>
    /// <param name="lockRequired">
    /// Whether the current schedule or temporary lock-condition input requires the lock.
    /// </param>
    /// <param name="restartWhenAvailable">
    /// The user setting for restarts while the lock is not required, or <see langword="null"/> when
    /// the setting is absent and the product default must be used.
    /// </param>
    /// <returns>The restart and lock-recovery action to publish to the continuity supervisor.</returns>
    public static RestartContinuityDecision Decide(
        bool lockRequired,
        bool? restartWhenAvailable)
    {
        if (lockRequired)
        {
            return new RestartContinuityDecision(
                RestartRequired: true,
                RecoverLock: true);
        }

        bool restartRequired =
            restartWhenAvailable ?? DefaultRestartWhenAvailable;
        return new RestartContinuityDecision(
            restartRequired,
            RecoverLock: false);
    }
}
