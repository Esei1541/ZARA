using System.Diagnostics;
using Zara.Core.UsagePolicy;

namespace Zara.Application.UsagePolicy;

/// <summary>Coordinates reminders and isolates audio failures from policy enforcement.</summary>
public sealed class LockReminderRuntime
{
    private readonly TimeProvider _timeProvider;
    private readonly ILockReminderAudioPort _audio;
    private readonly LockReminderCoordinator _coordinator;
    private UsagePolicyRuntimeSnapshot? _snapshot;
    private LockReminderSettings _settings = LockReminderSettings.Default;
    private PendingReminder? _pending;

    /// <summary>Creates the reminder runtime for the user's desktop session.</summary>
    public LockReminderRuntime(TimeProvider timeProvider, ILockReminderAudioPort audio)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _coordinator = new LockReminderCoordinator(timeProvider);
    }

    /// <summary>Observes the latest policy result on the caller's serialized desktop context.</summary>
    public void Observe(UsagePolicyRuntimeSnapshot snapshot, LockReminderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        _snapshot = snapshot;
        _settings = settings;
        if (_pending is not null && !IsCurrent(_pending))
        {
            Stop();
        }

        int? minutes = _coordinator.Evaluate(snapshot, settings);
        if (minutes is not int leadTime || snapshot.NextLockStartLocalTime is not DateTime lockStart)
        {
            return;
        }

        var pending = new PendingReminder(leadTime, lockStart, _timeProvider.GetTimestamp());
        _pending = pending;
#pragma warning disable CA1031 // Optional audio must never interrupt lock enforcement.
        try
        {
            _audio.Play(leadTime, () =>
                IsCurrent(pending) &&
                // A local clip may take a moment to open, but must not start a stale announcement.
                _timeProvider.GetElapsedTime(pending.RequestedTimestamp) <= TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            Trace.TraceError("The lock reminder could not start: {0}", exception);
            Stop();
        }
#pragma warning restore CA1031
    }

    /// <summary>Discards missed boundaries after a Windows clock or power change.</summary>
    public void ResetObservation()
    {
        _coordinator.ResetObservation();
        Stop();
    }

    /// <summary>Stops reminder audio without changing the lock policy.</summary>
    public void Stop()
    {
        _pending = null;
#pragma warning disable CA1031 // Cleanup failures in optional audio must not block locking or exit.
        try
        {
            _audio.StopPlayback();
        }
        catch (Exception exception)
        {
            Trace.TraceError("The lock reminder could not stop: {0}", exception);
        }
#pragma warning restore CA1031
    }

    private bool IsCurrent(PendingReminder pending)
    {
        UsagePolicyRuntimeSnapshot? snapshot = _snapshot;
        if (!ReferenceEquals(_pending, pending) || snapshot is null ||
            !_settings.IsEnabled(pending.Minutes) || snapshot.Evaluation.LockRequired ||
            snapshot.NextLockStartLocalTime is not DateTime currentStart ||
            (currentStart - pending.LockStart).Duration() > TimeSpan.FromSeconds(2))
        {
            return false;
        }

        DateTime localNow = _timeProvider.GetLocalNow().DateTime;
        return localNow < currentStart &&
            localNow >= currentStart.AddMinutes(-pending.Minutes).AddSeconds(-2) &&
            !UsagePolicyEvaluator.Evaluate(
                snapshot.Settings,
                localNow,
                snapshot.EmergencyUnlockEndLocalTime > localNow).LockRequired;
    }

    private sealed record PendingReminder(int Minutes, DateTime LockStart, long RequestedTimestamp);
}
