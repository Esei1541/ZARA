using System.Text.Json.Serialization;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Stores the user's desktop restart and spoken lock reminder preferences.
/// </summary>
/// <param name="RestartOnExitWhenUnlocked">
/// Whether the desktop process should restart after exiting while no lock is active.
/// </param>
/// <param name="VoiceReminder30Minutes">Whether to speak the 30-minute lock reminder.</param>
/// <param name="VoiceReminder10Minutes">Whether to speak the 10-minute lock reminder.</param>
/// <param name="VoiceReminder5Minutes">Whether to speak the 5-minute lock reminder.</param>
/// <param name="VoiceReminder1Minute">Whether to speak the 1-minute lock reminder.</param>
public sealed record DesktopRestartSettings(
    [property: JsonPropertyName("restartOnExitWhenUnlocked")]
    bool RestartOnExitWhenUnlocked = true,
    [property: JsonPropertyName("voiceReminder30Minutes")]
    bool VoiceReminder30Minutes = true,
    [property: JsonPropertyName("voiceReminder10Minutes")]
    bool VoiceReminder10Minutes = true,
    [property: JsonPropertyName("voiceReminder5Minutes")]
    bool VoiceReminder5Minutes = true,
    [property: JsonPropertyName("voiceReminder1Minute")]
    bool VoiceReminder1Minute = true)
{
    /// <summary>
    /// Gets the settings used when no persisted document exists or a corrupt document is quarantined.
    /// </summary>
    public static DesktopRestartSettings Default { get; } = new();
}
