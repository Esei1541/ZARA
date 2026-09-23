namespace Zara.Core.UsagePolicy;

/// <summary>Persists consumed unlocks independently of an in-memory timed unlock.</summary>
public sealed record EmergencyUnlockUsage
{
    /// <summary>Creates a validated usage record.</summary>
    public EmergencyUnlockUsage(int usedCount = 0, DateTime? nextResetLocalTime = null)
    {
        if (usedCount is < 0 or > EmergencyUnlockSettings.MaximumWeeklyMaximumCount)
        {
            throw new ArgumentOutOfRangeException(nameof(usedCount));
        }

        if ((usedCount > 0 && nextResetLocalTime is null) ||
            (nextResetLocalTime is DateTime reset && reset.TimeOfDay != TimeSpan.Zero))
        {
            throw new ArgumentException("Usage must have a midnight reset boundary.", nameof(nextResetLocalTime));
        }

        UsedCount = usedCount;
        NextResetLocalTime = nextResetLocalTime;
    }

    /// <summary>Gets the count consumed in the current period.</summary>
    public int UsedCount { get; }

    /// <summary>Gets the next local reset boundary, retained across clock rollbacks.</summary>
    public DateTime? NextResetLocalTime { get; }

    /// <summary>Gets unused initial state.</summary>
    public static EmergencyUnlockUsage Empty { get; } = new();

    /// <summary>Advances expired periods once without accumulating unused allowances.</summary>
    public EmergencyUnlockUsage Refresh(EmergencyUnlockSettings settings, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (NextResetLocalTime is null && !settings.WeeklyLimitEnabled)
        {
            return this;
        }

        return NextResetLocalTime is null || localNow >= NextResetLocalTime.Value
            ? new EmergencyUnlockUsage(0, FindNextReset(settings.WeeklyResetDay, localNow))
            : this;
    }

    /// <summary>Preserves consumption when editing limits and moves only a changed weekday boundary.</summary>
    public EmergencyUnlockUsage ChangeSettings(
        EmergencyUnlockSettings previous,
        EmergencyUnlockSettings updated,
        DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(updated);
        EmergencyUnlockUsage current = Refresh(previous, localNow);
        if (previous.WeeklyResetDay != updated.WeeklyResetDay &&
            (current.NextResetLocalTime is not null || updated.WeeklyLimitEnabled))
        {
            return new EmergencyUnlockUsage(current.UsedCount, FindNextReset(updated.WeeklyResetDay, localNow));
        }

        return current.Refresh(updated, localNow);
    }

    /// <summary>Gets the remaining allowance, or null when the limit is disabled.</summary>
    public int? GetRemainingCount(EmergencyUnlockSettings settings, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.WeeklyLimitEnabled
            ? Math.Max(0, settings.WeeklyMaximumCount - Refresh(settings, localNow).UsedCount)
            : null;
    }

    /// <summary>Consumes one allowance after the caller has validated an unlock request.</summary>
    public EmergencyUnlockUsage Consume(EmergencyUnlockSettings settings, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EmergencyUnlockUsage current = Refresh(settings, localNow);
        if (!settings.WeeklyLimitEnabled)
        {
            return current;
        }

        if (current.UsedCount >= settings.WeeklyMaximumCount)
        {
            throw new InvalidOperationException("이번 주 긴급 해제 횟수를 모두 사용했습니다.");
        }

        return new EmergencyUnlockUsage(current.UsedCount + 1, current.NextResetLocalTime);
    }

    private static DateTime FindNextReset(DayOfWeek day, DateTime localNow)
    {
        int days = ((int)day - (int)localNow.DayOfWeek + 7) % 7;
        return localNow.Date.AddDays(days == 0 ? 7 : days);
    }
}
