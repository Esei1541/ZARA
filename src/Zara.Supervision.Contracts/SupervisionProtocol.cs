namespace Zara.Supervision.Contracts;

/// <summary>
/// Defines the bounded local IPC protocol shared by the desktop client and the Windows Service.
/// </summary>
public static class SupervisionProtocol
{
    /// <summary>The only protocol version accepted by this build.</summary>
    public const int CurrentVersion = 3;

    /// <summary>The machine-local named pipe owned by the ZARA Service.</summary>
    public const string PipeName = "ZARA.Supervision.v3";

    /// <summary>
    /// Marks a desktop process launched by the Service, either for the first interactive logon or
    /// after a supervised exit. The one-time launch token proves the exact process generation.
    /// </summary>
    public const string ServiceLaunchSwitch = "--zara-service-recovery";

    /// <summary>
    /// Compatibility alias for callers built before the Service could start the first desktop after
    /// interactive logon.
    /// </summary>
    public const string RecoverySwitch = ServiceLaunchSwitch;

    /// <summary>
    /// Marks the exact process exit that follows an acknowledged explicit-exit release. The Service
    /// must not suppress recovery for the same process when it exits with any other code.
    /// </summary>
    public const int ExplicitExitCode = 0x5A415241;

    /// <summary>Limits one serialized protocol message to prevent unbounded allocations.</summary>
    public const int MaximumMessageCharacters = 16 * 1024;
}

/// <summary>
/// Enumerates the only requests that a desktop process may send to the Service.
/// </summary>
public enum SupervisionRequestKind
{
    Register = 1,
    UpdateLease = 2,
    ReportHealthy = 3,
    ReleaseForExplicitExit = 4,
    CommitLease = 5,
    RestrictTaskManager = 6,
    RestoreTaskManager = 7,
}

/// <summary>
/// Enumerates Service responses without exposing arbitrary command execution over IPC.
/// </summary>
public enum SupervisionResponseKind
{
    Registered = 1,
    LeaseAcknowledged = 2,
    HealthyAcknowledged = 3,
    ExitAcknowledged = 4,
    Rejected = 5,
    LeasePrepared = 6,
    TaskManagerRestricted = 7,
    TaskManagerRestored = 8,
}

/// <summary>
/// Identifies the connecting desktop instance. The Service verifies every field against the named
/// pipe client process and its Windows access token instead of trusting these values by themselves.
/// </summary>
/// <param name="ProcessId">The desktop process identifier.</param>
/// <param name="ProcessStartTimeUtcTicks">The exact process creation time in UTC ticks.</param>
/// <param name="SessionId">The interactive Windows session identifier.</param>
/// <param name="UserSid">The Windows user SID claimed by the desktop.</param>
public sealed record SupervisedProcessIdentity(
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    int SessionId,
    string UserSid);

/// <summary>
/// Carries the final restart instruction calculated outside the Service.
/// </summary>
/// <param name="Revision">A monotonically increasing revision within the desktop generation.</param>
/// <param name="RestartRequiredAfterExit">Whether the Service must recreate the desktop after exit.</param>
/// <param name="RecoverLockOnRestart">Whether the recreated desktop must re-evaluate and restore lock UI.</param>
public sealed record SupervisionLease(
    long Revision,
    bool RestartRequiredAfterExit,
    bool RecoverLockOnRestart);

/// <summary>
/// Represents one versioned desktop request with a strict field shape selected by
/// <see cref="Kind" />.
/// </summary>
/// <param name="ProtocolVersion">The desktop protocol version.</param>
/// <param name="Kind">The request category that selects the allowed fields.</param>
/// <param name="Process">
/// Required only for <see cref="SupervisionRequestKind.Register" /> and null for every other kind.
/// </param>
/// <param name="Lease">
/// Required for <see cref="SupervisionRequestKind.Register" />,
/// <see cref="SupervisionRequestKind.UpdateLease" />, and
/// <see cref="SupervisionRequestKind.ReleaseForExplicitExit" />. It is null for
/// <see cref="SupervisionRequestKind.ReportHealthy" />.
/// </param>
/// <param name="Revision">
/// Required only for <see cref="SupervisionRequestKind.ReportHealthy" /> and
/// <see cref="SupervisionRequestKind.CommitLease" />, and null for every other kind. This field
/// prevents a confirmation from carrying a second, different lease payload.
/// </param>
/// <param name="LaunchToken">
/// An optional one-time token for a process launched by the Service. It is used only for
/// <see cref="SupervisionRequestKind.Register" /> and is null for every other kind.
/// </param>
/// <remarks>
/// A registration lease has revision zero and is accepted atomically with the process identity.
/// A protection-decreasing update is prepared by the Service and becomes authoritative only after
/// the ordered revision-only <see cref="SupervisionRequestKind.CommitLease" /> confirmation.
/// An explicit-exit lease carries the ordered release revision with both restart flags cleared.
/// The Service rejects every request whose unused fields are not null or whose required fields are
/// absent.
/// </remarks>
public sealed record SupervisionRequest(
    int ProtocolVersion,
    SupervisionRequestKind Kind,
    SupervisedProcessIdentity? Process,
    SupervisionLease? Lease,
    long? Revision,
    string? LaunchToken);

/// <summary>
/// Represents a bounded Service response and the last durable lease revision it accepted.
/// </summary>
/// <param name="ProtocolVersion">The Service protocol version.</param>
/// <param name="Kind">The response category.</param>
/// <param name="AcknowledgedRevision">The accepted lease revision, or zero before a lease exists.</param>
/// <param name="RecoverLockOnStart">Whether this new desktop must immediately re-evaluate lock UI.</param>
/// <param name="ErrorCode">A stable internal diagnostic code for rejected requests.</param>
public sealed record SupervisionResponse(
    int ProtocolVersion,
    SupervisionResponseKind Kind,
    long AcknowledgedRevision,
    bool RecoverLockOnStart,
    string? ErrorCode);
