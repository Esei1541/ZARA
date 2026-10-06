namespace Zara.Core.UsagePolicy;

/// <summary>
/// Represents one interval, optionally ending the following day, that bypasses normal restrictions.
/// </summary>
/// <param name="id">The stable identifier used for deletion.</param>
/// <param name="date">The local calendar date on which the reservation starts.</param>
/// <param name="startTime">The inclusive local start time.</param>
/// <param name="endTime">The exclusive local end time.</param>
/// <param name="memo">User-visible text that does not affect rule evaluation.</param>
public sealed record OutOfHoursReservation
{
    /// <summary>Gets the stable identifier used for deletion.</summary>
    public Guid Id { get; }

    /// <summary>Gets the local calendar date to which the reservation belongs.</summary>
    public DateOnly Date { get; }

    /// <summary>Gets the inclusive local start time.</summary>
    public TimeOnly StartTime { get; }

    /// <summary>Gets the exclusive local end time.</summary>
    public TimeOnly EndTime { get; }

    /// <summary>Gets user-visible text that does not affect rule evaluation.</summary>
    public string Memo { get; }

    /// <summary>
    /// Validates a reservation when it is created.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The identifier is empty, the times match, or the following date cannot be represented.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="Memo"/> is null.</exception>
    public OutOfHoursReservation(
        Guid id,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        string memo)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A reservation identifier is required.", nameof(id));
        }

        if (startTime == endTime)
        {
            throw new ArgumentException(
                "A reservation must have different start and end times.",
                nameof(endTime));
        }

        if (endTime < startTime && date == DateOnly.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(date), "An overnight reservation requires a following date.");
        }

        ArgumentNullException.ThrowIfNull(memo);
        Id = id;
        Date = date;
        StartTime = startTime;
        EndTime = endTime;
        Memo = memo;
    }

    /// <summary>Gets the inclusive local start date and time.</summary>
    public DateTime GetStartLocalTime() => Date.ToDateTime(StartTime);

    /// <summary>Gets the exclusive local end, advancing the date when the clock wraps.</summary>
    public DateTime GetEndLocalTime() =>
        (EndTime < StartTime ? Date.AddDays(1) : Date).ToDateTime(EndTime);

    /// <summary>Checks both dates using an inclusive start and exclusive end.</summary>
    public bool Contains(DateTime localNow) =>
        GetStartLocalTime() <= localNow && localNow < GetEndLocalTime();
}
