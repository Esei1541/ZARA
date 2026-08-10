using Zara.Core.UsagePolicy;

namespace Zara.Application.UsagePolicy;

/// <summary>
/// Loads and atomically replaces the complete persisted time-rule settings for one Windows user.
/// </summary>
public interface IUsagePolicySettingsStore
{
    /// <summary>
    /// Loads the last valid settings snapshot or the product defaults when no valid document exists.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for or reading the persisted document.</param>
    /// <returns>A complete immutable settings snapshot.</returns>
    Task<UsagePolicySettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically persists the complete settings snapshot.
    /// </summary>
    /// <param name="settings">The snapshot to persist.</param>
    /// <param name="cancellationToken">Cancels persistence before the new snapshot is committed.</param>
    /// <returns>A task that completes after the snapshot is durable.</returns>
    Task SaveAsync(UsagePolicySettings settings, CancellationToken cancellationToken = default);
}
