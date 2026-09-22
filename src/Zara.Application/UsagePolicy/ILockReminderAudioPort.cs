namespace Zara.Application.UsagePolicy;

/// <summary>Plays a bundled reminder without waiting for audio loading or completion.</summary>
public interface ILockReminderAudioPort
{
    /// <summary>Starts the selected clip only if it is still valid after asynchronous loading.</summary>
    void Play(int minutes, Func<bool> isStillValid);

    /// <summary>Cancels pending loading and stops any current reminder.</summary>
    void StopPlayback();
}
