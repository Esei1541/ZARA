namespace Zara.Application.SystemPower;

/// <summary>
/// Describes how a previously accepted Windows shutdown cancellation was reconciled.
/// </summary>
public enum ShutdownCancellationRecoveryResult
{
    /// <summary>
    /// No accepted ZARA shutdown request remained to recover.
    /// </summary>
    NoAcceptedRequest,

    /// <summary>
    /// The lock that preceded the shutdown request was restored.
    /// </summary>
    LockRestored,

    /// <summary>
    /// The accepted request did not begin from a locked state, so no lock was restored.
    /// </summary>
    LockNotRequired,

    /// <summary>
    /// A newer explicit lock or safety-unlock intent superseded the stale recovery request.
    /// </summary>
    SupersededByNewerIntent,
}

/// <summary>
/// Safely removes ZARA lock effects before requesting a normal operating-system shutdown.
/// </summary>
public interface ISystemShutdownUseCase
{
    /// <summary>
    /// Requests one normal operating-system shutdown after the lock runtime is safe to exit.
    /// </summary>
    /// <param name="cancellationToken">Cancels cleanup or the pending platform request.</param>
    /// <returns>
    /// A task that completes after the operating system accepts the request. Repeated requests
    /// after acceptance complete without issuing another platform request.
    /// </returns>
    Task RequestShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests shutdown under a caller-issued attempt identity used to reject stale guard signals.
    /// </summary>
    /// <param name="requestId">A nonempty identity unique to this explicit shutdown attempt.</param>
    /// <param name="cancellationToken">Cancels cleanup or the pending platform request.</param>
    Task RequestShutdownAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles a Windows shutdown that was accepted and then canceled by the operating system.
    /// </summary>
    /// <returns>
    /// A task containing the recovery result. Safety restoration is intentionally non-cancelable
    /// once this method begins.
    /// </returns>
    Task<ShutdownCancellationRecoveryResult> HandleShutdownCancellationAsync();

    /// <summary>
    /// Reconciles cancellation only when it belongs to the matching accepted or in-flight request.
    /// </summary>
    /// <param name="requestId">The identity issued with the shutdown request.</param>
    Task<ShutdownCancellationRecoveryResult> HandleShutdownCancellationAsync(Guid requestId);
}
