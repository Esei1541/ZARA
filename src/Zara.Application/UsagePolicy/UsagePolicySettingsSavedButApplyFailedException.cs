namespace Zara.Application.UsagePolicy;

/// <summary>
/// Reports that usage-policy settings are already durable but applying their current lock decision
/// failed and will be retried by the runtime refresh loop.
/// </summary>
public sealed class UsagePolicySettingsSavedButApplyFailedException : InvalidOperationException
{
    /// <summary>Creates the partial-success result while preserving the apply failure.</summary>
    /// <param name="innerException">The failure raised after the settings store completed.</param>
    public UsagePolicySettingsSavedButApplyFailedException(Exception innerException)
        : base(
            "Usage-policy settings were saved, but their current lock decision could not be applied.",
            innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
    }
}
