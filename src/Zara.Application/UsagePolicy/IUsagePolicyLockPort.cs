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
    /// <see langword="true"/> when the overlay and restart continuity must be active;
    /// otherwise <see langword="false"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels applying the request.</param>
    /// <returns>A task that completes after the existing lock path acknowledges the change.</returns>
    Task ApplyPolicyLockRequirementAsync(
        bool lockRequired,
        CancellationToken cancellationToken = default);
}
