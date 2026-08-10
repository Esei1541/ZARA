using System.Text.Json.Serialization;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Stores the user preference that keeps the desktop process present outside an active lock.
/// </summary>
/// <param name="RestartOnExitWhenUnlocked">
/// Whether the desktop process should restart after exiting while no lock is active.
/// </param>
public sealed record DesktopRestartSettings(
    [property: JsonPropertyName("restartOnExitWhenUnlocked")]
    bool RestartOnExitWhenUnlocked = true)
{
    /// <summary>
    /// Gets the settings used when no persisted document exists or a corrupt document is quarantined.
    /// </summary>
    public static DesktopRestartSettings Default { get; } = new();
}
