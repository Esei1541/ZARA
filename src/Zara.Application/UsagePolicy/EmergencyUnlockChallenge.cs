namespace Zara.Application.UsagePolicy;

/// <summary>
/// Holds the in-memory prompt text that must be entered to begin one timed emergency unlock.
/// </summary>
/// <param name="sentences">The exact sentence sequence selected for the current request.</param>
public sealed record EmergencyUnlockChallenge(IReadOnlyList<string> Sentences)
{
    /// <summary>
    /// Gets the displayed text as one line per prompt, using a line-feed independent of the UI's
    /// platform-specific text-box line endings.
    /// </summary>
    public string ExpectedText => string.Join('\n', Sentences);
}
