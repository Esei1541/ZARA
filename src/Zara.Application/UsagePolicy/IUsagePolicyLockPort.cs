namespace Zara.Application.UsagePolicy;

/// <summary>
/// Applies a fully evaluated product time-rule decision to the existing lock and continuity path.
/// </summary>
public interface IUsagePolicyLockPort
{
    /// <summary>
    /// Applies the latest product lock requirement.
    /// </summary>
    /// <param name="lockRequired">
    /// Whether the overlay must currently be visible.
    /// </param>
    /// <param name="lockRequiredAfterRestart">
    /// Whether process restart and lock recovery remain required, even during an emergency unlock.
    /// </param>
    /// <param name="cancellationToken">Cancels applying the request.</param>
    /// <returns>A task that completes after the existing lock path acknowledges the change.</returns>
    Task ApplyPolicyLockRequirementAsync(
        bool lockRequired,
        bool lockRequiredAfterRestart,
        CancellationToken cancellationToken = default);
}
