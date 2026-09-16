namespace Zara.Application.Locking;

/// <summary>
/// Restricts shell shortcuts in the interactive user session while the lock is visible.
/// </summary>
public interface ILockInputPort
{
    /// <summary>
    /// Enables the restriction, or confirms that it is already enabled.
    /// </summary>
    Task EnableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Restores normal input and releases the restriction before returning.
    /// </summary>
    Task DisableAsync(CancellationToken cancellationToken);
}
