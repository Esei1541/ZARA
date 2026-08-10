using Zara.Core.Continuity;
using Zara.Core.Runtime;

namespace Zara.Application.Continuity;

/// <summary>
/// Publishes one acknowledged process-continuity state to an external supervisor.
/// </summary>
/// <param name="Revision">
/// The application-issued monotonically increasing revision shared by publish and release requests.
/// </param>
/// <param name="LockRequired">
/// Whether the current schedule or temporary condition requires lock recovery after a restart.
/// </param>
/// <param name="RestartWhenAvailable">
/// The normalized user setting used while <paramref name="LockRequired"/> is
/// <see langword="false"/>.
/// </param>
/// <param name="OverlayProjection">
/// The last known overlay projection, recorded independently from <paramref name="LockRequired"/>.
/// </param>
/// <param name="Decision">The Core decision that the supervisor must enforce after process exit.</param>
public sealed record RestartContinuityLease(
    long Revision,
    bool LockRequired,
    bool RestartWhenAvailable,
    OverlayProjectionState OverlayProjection,
    RestartContinuityDecision Decision);

/// <summary>
/// Revokes process supervision before an explicit tray exit.
/// </summary>
/// <param name="Revision">
/// The application-issued revision that orders this release after every earlier lease.
/// </param>
public sealed record RestartContinuityRelease(long Revision);

/// <summary>
/// Confirms that the external supervisor accepted one exact publish or release revision.
/// </summary>
/// <param name="Revision">The exact accepted application revision.</param>
public sealed record RestartContinuityAcknowledgement(long Revision);

/// <summary>
/// Exchanges ordered continuity leases and explicit releases with an external supervisor.
/// </summary>
/// <remarks>
/// Implementations must acknowledge only after the supplied revision becomes the supervisor's
/// accepted state. Rejected requests and transport failures must complete with an exception rather
/// than acknowledging a different revision.
/// </remarks>
public interface IRestartContinuityPort
{
    /// <summary>
    /// Publishes the latest restart decision and its independent lock and overlay observations.
    /// </summary>
    /// <param name="lease">The full continuity state to accept atomically.</param>
    /// <param name="cancellationToken">Cancels the pending publish.</param>
    /// <returns>An acknowledgement for the exact accepted revision.</returns>
    Task<RestartContinuityAcknowledgement> PublishAsync(
        RestartContinuityLease lease,
        CancellationToken cancellationToken);

    /// <summary>
    /// Revokes supervision so an explicit tray exit does not cause a restart.
    /// </summary>
    /// <param name="release">The ordered release request.</param>
    /// <param name="cancellationToken">Cancels the pending release.</param>
    /// <returns>An acknowledgement for the exact accepted revision.</returns>
    Task<RestartContinuityAcknowledgement> ReleaseAsync(
        RestartContinuityRelease release,
        CancellationToken cancellationToken);
}
