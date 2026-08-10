namespace Zara.Core.UsagePolicy;

/// <summary>
/// Defines one weekday's optional period during which normal computer use is restricted.
/// </summary>
/// <remarks>
/// When the start time is later than the release time, the restriction continues into the next
/// calendar day. Equal start and release times are intentionally rejected because they do not
/// distinguish a disabled rule from an all-day rule.
/// </remarks>
/// <param name="isEnabled">Whether this weekday's restriction is active.</param>
/// <param name="startTime">The local time at which the restriction begins.</param>
/// <param name="releaseTime">The local time at which the restriction ends.</param>
public sealed record DailyUsageRestriction
{
    /// <summary>
    /// Gets a disabled rule used by the initial configuration.
    /// </summary>
    public static DailyUsageRestriction Disabled { get; } = new(
        isEnabled: false,
        startTime: TimeOnly.MinValue,
        releaseTime: TimeOnly.MinValue);

    /// <summary>Gets whether this weekday's restriction is active.</summary>
    public bool IsEnabled { get; }

    /// <summary>Gets the local time at which the restriction begins.</summary>
    public TimeOnly StartTime { get; }

    /// <summary>Gets the local time at which the restriction ends.</summary>
    public TimeOnly ReleaseTime { get; }

    /// <summary>
    /// Validates an enabled rule when it is created.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="startTime"/> and <paramref name="releaseTime"/> are equal for an
    /// enabled rule.
    /// </exception>
    public DailyUsageRestriction(
        bool isEnabled,
        TimeOnly startTime,
        TimeOnly releaseTime)
    {
        if (isEnabled && startTime == releaseTime)
        {
            throw new ArgumentException(
                "An enabled usage restriction requires different start and release times.",
                nameof(releaseTime));
        }

        IsEnabled = isEnabled;
        StartTime = startTime;
        ReleaseTime = releaseTime;
    }
}
