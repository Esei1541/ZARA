namespace Zara.Infrastructure.Windows.Continuity;

/// <summary>Preserves the protocol rejection code without exposing launch credentials.</summary>
public sealed class SupervisionRegistrationException(string errorCode)
    : InvalidOperationException($"The ZARA Service rejected desktop registration ({errorCode}).")
{
    public string ErrorCode { get; } = errorCode;

    public bool RequiresServiceDesktop => ErrorCode is "SERVICE_LAUNCH_TOKEN_REQUIRED" or "RECOVERY_TOKEN_REQUIRED";
}
