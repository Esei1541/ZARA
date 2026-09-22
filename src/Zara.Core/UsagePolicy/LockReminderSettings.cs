namespace Zara.Core.UsagePolicy;

/// <summary>Controls each spoken reminder before the next actual lock.</summary>
public sealed record LockReminderSettings(
    bool ThirtyMinutes = true,
    bool TenMinutes = true,
    bool FiveMinutes = true,
    bool OneMinute = true)
{
    /// <summary>Gets the initial preferences, with every reminder enabled.</summary>
    public static LockReminderSettings Default { get; } = new();

    /// <summary>Gets whether the specified supported lead time is enabled.</summary>
    public bool IsEnabled(int minutes) => minutes switch
    {
        30 => ThirtyMinutes,
        10 => TenMinutes,
        5 => FiveMinutes,
        1 => OneMinute,
        _ => throw new ArgumentOutOfRangeException(nameof(minutes)),
    };

    /// <summary>Changes one reminder while retaining the other preferences.</summary>
    public LockReminderSettings WithEnabled(int minutes, bool enabled) => minutes switch
    {
        30 => this with { ThirtyMinutes = enabled },
        10 => this with { TenMinutes = enabled },
        5 => this with { FiveMinutes = enabled },
        1 => this with { OneMinute = enabled },
        _ => throw new ArgumentOutOfRangeException(nameof(minutes)),
    };
}
