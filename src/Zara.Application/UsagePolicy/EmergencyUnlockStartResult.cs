namespace Zara.Application.UsagePolicy;

/// <summary>
/// Describes whether a request immediately started an emergency unlock or requires typed prompts.
/// </summary>
/// <param name="StartedImmediately">
/// Whether the configured zero-prompt request has already started the timed emergency unlock.
/// </param>
/// <param name="Challenge">The in-memory challenge required before unlocking, when applicable.</param>
public sealed record EmergencyUnlockStartResult(
    bool StartedImmediately,
    EmergencyUnlockChallenge? Challenge);
