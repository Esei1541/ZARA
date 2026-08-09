namespace Zara.Application.SystemPower;

/// <summary>
/// Requests a normal, non-forced operating-system shutdown through a platform adapter.
/// </summary>
/// <remarks>
/// A completed request means that the operating system accepted the shutdown request; it does not
/// mean that the operating system has finished shutting down. Implementations must not report
/// cancellation after the irreversible platform request has been accepted.
/// </remarks>
public interface ISystemShutdownPort
{
    /// <summary>
    /// Requests a normal operating-system shutdown without forcing applications to terminate.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the operation before the platform shutdown request is accepted.
    /// </param>
    /// <returns>A task that completes after the operating system accepts the request.</returns>
    Task RequestShutdownAsync(CancellationToken cancellationToken);
}
