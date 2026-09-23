using System.Diagnostics;
using Zara.Core.UsagePolicy;

namespace Zara.Application.UsagePolicy;

/// <summary>Coordinates reminders and isolates audio failures from policy enforcement.</summary>
public sealed class LockReminderRuntime
{
    private readonly TimeProvider _timeProvider;
    private readonly ILockReminderAudioPort _audio;
    private readonly LockReminderCoordinator _coordinator;
    private readonly LockReminderDiagnostics _diagnostics;
    private UsagePolicyRuntimeSnapshot? _snapshot;
    private LockReminderSettings _settings = LockReminderSettings.Default;
    private PendingReminder? _pending;

    /// <summary>Creates the reminder runtime for the user's desktop session.</summary>
    public LockReminderRuntime(TimeProvider timeProvider, ILockReminderAudioPort audio,
        LockReminderDiagnostics? diagnostics = null)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _diagnostics = diagnostics ?? new();
        _coordinator = new LockReminderCoordinator(timeProvider, _diagnostics);
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
            _diagnostics.Record(FormattableString.Invariant(
                $"cancelled reason=policy-or-setting-changed minutes={_pending.Minutes} target={_pending.LockStart:O}"));
            Stop();
        }

        int? minutes = _coordinator.Evaluate(snapshot, settings);
        if (minutes is not int leadTime || snapshot.NextLockStartLocalTime is not DateTime lockStart)
        {
            return;
        }

        var pending = new PendingReminder(leadTime, lockStart, _timeProvider.GetTimestamp());
        _pending = pending;
        _diagnostics.Record(FormattableString.Invariant($"play-request minutes={leadTime} target={lockStart:O}"));
#pragma warning disable CA1031 // Optional audio must never interrupt lock enforcement.
        try
        {
            _audio.Play(leadTime, () => CanStartPlayback(pending));
        }
        catch (Exception exception)
        {
            Trace.TraceError("The lock reminder could not start: {0}", exception);
            _diagnostics.Record($"play-failed minutes={leadTime} error={exception}");
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
        if (_pending is not null)
        {
            _diagnostics.Record(FormattableString.Invariant(
                $"stopped minutes={_pending.Minutes} target={_pending.LockStart:O}"));
        }
        _pending = null;
#pragma warning disable CA1031 // Cleanup failures in optional audio must not block locking or exit.
        try
        {
            _audio.StopPlayback();
        }
        catch (Exception exception)
        {
            Trace.TraceError("The lock reminder could not stop: {0}", exception);
            _diagnostics.Record($"stop-failed error={exception}");
        }
#pragma warning restore CA1031
    }

    private bool CanStartPlayback(PendingReminder pending)
    {
        bool current = IsCurrent(pending);
        TimeSpan elapsed = _timeProvider.GetElapsedTime(pending.RequestedTimestamp);
        bool ready = current && elapsed <= TimeSpan.FromSeconds(5);
        if (!ready)
        {
            string reason = current ? "media-load-late" : "policy-or-setting-changed";
            _diagnostics.Record(FormattableString.Invariant(
                $"play-cancelled reason={reason} minutes={pending.Minutes} elapsed={elapsed} target={pending.LockStart:O}"));
        }
        return ready;
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
