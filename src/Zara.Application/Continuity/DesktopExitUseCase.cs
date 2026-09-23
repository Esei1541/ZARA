using Zara.Application.Locking;
using Zara.Application.UsagePolicy;
using Zara.Core.Runtime;

namespace Zara.Application.Continuity;

/// <summary>
/// Cleans up a confirmed tray exit without revoking supervision when the lock must recover.
/// </summary>
public sealed class DesktopExitUseCase
{
    private readonly UsagePolicyRuntime _policy;
    private readonly ILockRuntimeUseCase _lockRuntime;
    private readonly IRestartContinuityUseCase _continuity;

    /// <summary>Creates the exit flow from the running policy, lock, and continuity owners.</summary>
    public DesktopExitUseCase(
        UsagePolicyRuntime policy,
        ILockRuntimeUseCase lockRuntime,
        IRestartContinuityUseCase continuity)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(lockRuntime);
        ArgumentNullException.ThrowIfNull(continuity);
        _policy = policy;
        _lockRuntime = lockRuntime;
        _continuity = continuity;
    }

    /// <summary>
    /// Re-evaluates time rules after confirmation, cleans up, then initiates process shutdown.
    /// Policy updates cannot interleave with exit preparation.
    /// </summary>
    /// <param name="shutdown">
    /// Initiates shutdown; true requires an ordinary exit preserving lock recovery, while false
    /// permits the acknowledged explicit-exit marker. The callback must not fail after initiation.
    /// </param>
    /// <param name="cancellationToken">Cancels preparation before shutdown.</param>
    /// <returns>A task that completes when shutdown has been initiated.</returns>
    public Task ExecuteAsync(Func<bool, Task> shutdown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        return _policy.ExecuteExitAsync(async token =>
        {
            await _lockRuntime.PrepareForExitAsync(token).ConfigureAwait(false);
            _ = await _continuity
                .PublishOverlayProjectionAsync(OverlayProjectionState.Hidden, token)
                .ConfigureAwait(false);
        }, async (changedLockRequirement, token) =>
        {
            RestartContinuityLease lease = changedLockRequirement is bool required
                ? await _continuity.PublishLockConditionAsync(required, token).ConfigureAwait(false)
                : _continuity.CurrentAcknowledgedLease ??
                    throw new InvalidOperationException("Exit requires acknowledged restart information.");
            token.ThrowIfCancellationRequested();
            if (!lease.LockRequired)
            {
                _ = await _continuity.ReleaseForExplicitExitAsync(token).ConfigureAwait(false);
            }

            await shutdown(lease.LockRequired).ConfigureAwait(false);
        }, cancellationToken);
    }
}
