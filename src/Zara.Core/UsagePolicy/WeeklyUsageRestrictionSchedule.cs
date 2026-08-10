namespace Zara.Core.UsagePolicy;

/// <summary>
/// Holds the independent usage restriction rule for every weekday.
/// </summary>
public sealed record WeeklyUsageRestrictionSchedule
{
    /// <summary>
    /// Creates a weekly schedule from its seven weekday rules.
    /// </summary>
    public WeeklyUsageRestrictionSchedule(
        DailyUsageRestriction monday,
        DailyUsageRestriction tuesday,
        DailyUsageRestriction wednesday,
        DailyUsageRestriction thursday,
        DailyUsageRestriction friday,
        DailyUsageRestriction saturday,
        DailyUsageRestriction sunday)
    {
        Monday = RequireRule(monday, nameof(monday));
        Tuesday = RequireRule(tuesday, nameof(tuesday));
        Wednesday = RequireRule(wednesday, nameof(wednesday));
        Thursday = RequireRule(thursday, nameof(thursday));
        Friday = RequireRule(friday, nameof(friday));
        Saturday = RequireRule(saturday, nameof(saturday));
        Sunday = RequireRule(sunday, nameof(sunday));
    }

    /// <summary>Gets the initially disabled schedule.</summary>
    public static WeeklyUsageRestrictionSchedule Default { get; } = new(
        DailyUsageRestriction.Disabled,
        DailyUsageRestriction.Disabled,
        DailyUsageRestriction.Disabled,
        DailyUsageRestriction.Disabled,
        DailyUsageRestriction.Disabled,
        DailyUsageRestriction.Disabled,
        DailyUsageRestriction.Disabled);

    /// <summary>Gets Monday's rule.</summary>
    public DailyUsageRestriction Monday { get; }

    /// <summary>Gets Tuesday's rule.</summary>
    public DailyUsageRestriction Tuesday { get; }

    /// <summary>Gets Wednesday's rule.</summary>
    public DailyUsageRestriction Wednesday { get; }

    /// <summary>Gets Thursday's rule.</summary>
    public DailyUsageRestriction Thursday { get; }

    /// <summary>Gets Friday's rule.</summary>
    public DailyUsageRestriction Friday { get; }

    /// <summary>Gets Saturday's rule.</summary>
    public DailyUsageRestriction Saturday { get; }

    /// <summary>Gets Sunday's rule.</summary>
    public DailyUsageRestriction Sunday { get; }

    /// <summary>
    /// Gets the rule that starts on <paramref name="dayOfWeek"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dayOfWeek"/> is not a defined weekday.
    /// </exception>
    public DailyUsageRestriction GetRestriction(DayOfWeek dayOfWeek) =>
        dayOfWeek switch
        {
            DayOfWeek.Monday => Monday,
            DayOfWeek.Tuesday => Tuesday,
            DayOfWeek.Wednesday => Wednesday,
            DayOfWeek.Thursday => Thursday,
            DayOfWeek.Friday => Friday,
            DayOfWeek.Saturday => Saturday,
            DayOfWeek.Sunday => Sunday,
            _ => throw new ArgumentOutOfRangeException(
                nameof(dayOfWeek),
                dayOfWeek,
                "The weekday is not defined."),
        };

    /// <summary>
    /// Returns a new schedule with only the requested weekday replaced.
    /// </summary>
    public WeeklyUsageRestrictionSchedule WithRestriction(
        DayOfWeek dayOfWeek,
        DailyUsageRestriction restriction)
    {
        ArgumentNullException.ThrowIfNull(restriction);

        return dayOfWeek switch
        {
            DayOfWeek.Monday => new(
                restriction, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday),
            DayOfWeek.Tuesday => new(
                Monday, restriction, Wednesday, Thursday, Friday, Saturday, Sunday),
            DayOfWeek.Wednesday => new(
                Monday, Tuesday, restriction, Thursday, Friday, Saturday, Sunday),
            DayOfWeek.Thursday => new(
                Monday, Tuesday, Wednesday, restriction, Friday, Saturday, Sunday),
            DayOfWeek.Friday => new(
                Monday, Tuesday, Wednesday, Thursday, restriction, Saturday, Sunday),
            DayOfWeek.Saturday => new(
                Monday, Tuesday, Wednesday, Thursday, Friday, restriction, Sunday),
            DayOfWeek.Sunday => new(
                Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, restriction),
            _ => throw new ArgumentOutOfRangeException(
                nameof(dayOfWeek),
                dayOfWeek,
                "The weekday is not defined."),
        };
    }

    private static DailyUsageRestriction RequireRule(
        DailyUsageRestriction? restriction,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(restriction, parameterName);
        return restriction;
    }
}
