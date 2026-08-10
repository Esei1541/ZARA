namespace Zara.Enforcement.Service.Supervision;

/// <summary>
/// Describes why the local supervisor changed its process-presence requirement.
/// This is an internal state-machine command, not an IPC protocol DTO.
/// </summary>
internal enum SupervisionDirectiveReason
{
    ServiceStarted,
    LeaseUpdated,
    ExplicitRelease,
    SessionEnding,
}

/// <summary>
/// Represents the latest immutable process-presence instruction already decided outside the
/// Service supervisor. The supervisor only enforces this value and does not interpret schedules
/// or user settings.
/// </summary>
/// <param name="Revision">A monotonically increasing revision within this Service lifetime.</param>
/// <param name="RestartRequired">Whether an exited desktop must be recreated.</param>
/// <param name="Reason">The technical source of the state transition.</param>
internal readonly record struct SupervisionDirective(
    long Revision,
    bool RestartRequired,
    SupervisionDirectiveReason Reason);

/// <summary>
/// Supplies a coalesced latest-value stream to the supervisor. IPC adapters translate versioned
/// protocol requests into this internal instruction before publishing them here.
/// </summary>
internal interface ISupervisionCommandSource
{
    /// <summary>Gets the latest accepted instruction without waiting.</summary>
    SupervisionDirective Current { get; }

    /// <summary>
    /// Waits until an instruction newer than <paramref name="observedRevision" /> is available.
    /// Multiple intermediate updates may be coalesced into the newest immutable value.
    /// </summary>
    ValueTask<SupervisionDirective> WaitForChangeAsync(
        long observedRevision,
        CancellationToken cancellationToken);
}

/// <summary>
/// Abstracts the exact user-session desktop launch so the retry state machine remains free of
/// Win32 process details.
/// </summary>
internal interface IDesktopProcessLauncher
{
    ValueTask<DesktopLaunchResult> LaunchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Creates a launch-scoped one-time authentication token and health completion owned by the exact
/// child generation. The IPC server validates the connecting process before completing it.
/// </summary>
internal interface IDesktopLaunchHandshakeFactory
{
    ValueTask<IDesktopLaunchHandshake> CreateAsync(
        int sessionId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Represents one launch token that cannot make a generation successful until authenticated IPC
/// registration and health acknowledgement complete.
/// </summary>
internal interface IDesktopLaunchHandshake : IAsyncDisposable
{
    string OneTimeToken { get; }

    /// <summary>
    /// Binds the token to the exact PID returned by CreateProcessAsUser before its primary thread
    /// resumes. The binding is immutable for the launch generation.
    /// </summary>
    bool TryBindProcess(int processId);

    Task WaitForHealthyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Represents the exact process handle owned by one successful launch generation.
/// </summary>
internal interface ISupervisedProcess : IAsyncDisposable
{
    int ProcessId { get; }

    int SessionId { get; }

    /// <summary>
    /// Completes only after the launched generation has registered and reported healthy through
    /// the authenticated IPC transport. Failure means the exact child must be cleaned up and
    /// retried without resetting backoff.
    /// </summary>
    Task WaitForReadyAsync(CancellationToken cancellationToken);

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Represents an immutable launch outcome. All failures are retryable while the latest directive
/// continues to require process presence.
/// </summary>
/// <param name="Process">The exact owned process on success, otherwise <see langword="null" />.</param>
/// <param name="NativeErrorCode">The Win32 error code associated with a failed attempt.</param>
/// <param name="FailureStage">A bounded diagnostic stage name for a failed attempt.</param>
internal readonly record struct DesktopLaunchResult(
    ISupervisedProcess? Process,
    int NativeErrorCode,
    string? FailureStage)
{
    public bool Succeeded => Process is not null;

    public static DesktopLaunchResult Success(ISupervisedProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new(process, 0, null);
    }

    public static DesktopLaunchResult RetryableFailure(int nativeErrorCode, string failureStage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureStage);
        return new(null, nativeErrorCode, failureStage);
    }
}

/// <summary>
/// Provides cancellable retry delays without coupling the state machine to wall-clock time.
/// </summary>
internal interface ISupervisionDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
