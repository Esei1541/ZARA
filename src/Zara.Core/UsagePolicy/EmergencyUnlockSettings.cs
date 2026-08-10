namespace Zara.Core.UsagePolicy;

/// <summary>
/// Configures the duration and prompt count used by the product emergency-unlock workflow.
/// </summary>
/// <param name="DurationMinutes">The requested unlock duration in whole minutes.</param>
/// <param name="SentenceCount">The number of prompts that must be entered before unlocking.</param>
public sealed record EmergencyUnlockSettings
{
    /// <summary>Gets the smallest allowed emergency-unlock duration.</summary>
    public const int MinimumDurationMinutes = 1;

    /// <summary>Gets the largest allowed emergency-unlock duration.</summary>
    public const int MaximumDurationMinutes = 60;

    /// <summary>Gets the smallest allowed prompt count.</summary>
    public const int MinimumSentenceCount = 0;

    /// <summary>Gets the largest allowed prompt count.</summary>
    public const int MaximumSentenceCount = 99;

    /// <summary>Gets the requested unlock duration in whole minutes.</summary>
    public int DurationMinutes { get; }

    /// <summary>Gets the number of prompts that must be entered before unlocking.</summary>
    public int SentenceCount { get; }

    /// <summary>Gets the initially selected ten-minute, three-prompt configuration.</summary>
    public static EmergencyUnlockSettings Default { get; } = new(
        durationMinutes: 10,
        sentenceCount: 3);

    /// <summary>
    /// Validates the user-configurable ranges.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value falls outside its supported inclusive range.
    /// </exception>
    public EmergencyUnlockSettings(int durationMinutes, int sentenceCount)
    {
        if (durationMinutes is < MinimumDurationMinutes or > MaximumDurationMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMinutes),
                durationMinutes,
                $"The duration must be between {MinimumDurationMinutes} and " +
                $"{MaximumDurationMinutes} minutes.");
        }

        if (sentenceCount is < MinimumSentenceCount or > MaximumSentenceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sentenceCount),
                sentenceCount,
                $"The sentence count must be between {MinimumSentenceCount} and " +
                $"{MaximumSentenceCount}.");
        }

        DurationMinutes = durationMinutes;
        SentenceCount = sentenceCount;
    }
}
