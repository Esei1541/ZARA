namespace Zara.Application.Locking;

/// <summary>Identifies a continuous lock-failure episode for recovery and one-time notification.</summary>
/// <param name="IsRecovering">Whether failed lock effects are waiting for or undergoing a retry.</param>
/// <param name="Episode">The increasing identity of the current or most recent failure episode.</param>
public sealed record LockRecoveryState(bool IsRecovering, long Episode);
