namespace Zara.Application.UsagePolicy;

/// <summary>
/// Indicates that a setting or reservation mutation was requested during a base usage restriction.
/// </summary>
public sealed class UsagePolicySettingsLockedException : InvalidOperationException
{
    /// <summary>
    /// Creates the standard exception used by the time-rule command boundary.
    /// </summary>
    public UsagePolicySettingsLockedException()
        : base("Settings cannot be changed during the configured usage restriction.")
    {
    }
}
