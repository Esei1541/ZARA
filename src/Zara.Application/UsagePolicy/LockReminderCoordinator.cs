using Zara.Core.UsagePolicy;

namespace Zara.Application.UsagePolicy;

/// <summary>
/// Decides which one spoken reminder should be requested before the next actual policy lock.
/// </summary>
public sealed class LockReminderCoordinator(TimeProvider timeProvider, LockReminderDiagnostics? diagnostics = null)
{
    private static readonly int[] ReminderMinutes = [30, 10, 5, 1];
    private static readonly TimeSpan SameTargetTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReminderLateness = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClockChangeTolerance = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider = timeProvider ??
        throw new ArgumentNullException(nameof(timeProvider));
    private readonly HashSet<int> _observedReminderMinutes = [];
    private DateTime? _observedTargetLocalTime;
    private DateTime? _lastObservedLocalTime;
    private long? _lastObservedTimestamp;
    private LockReminderSettings? _lastSettings;
    private bool _resetPending = true;
    private readonly LockReminderDiagnostics _diagnostics = diagnostics ?? new();
    private bool? _lastLockRequired;
    private DateTime? _lastDiagnosticTarget;

    /// <summary>
    /// Returns the supported lead time whose reminder should be played now, or null.
    /// </summary>
    public int? Evaluate(
        UsagePolicyRuntimeSnapshot snapshot,
        LockReminderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        DateTime observedLocalTime = _timeProvider.GetLocalNow().DateTime;
        long observedTimestamp = _timeProvider.GetTimestamp();
        DateTime? nextLockStartLocalTime = snapshot.NextLockStartLocalTime;
        if (_lastSettings != settings || _lastLockRequired != snapshot.Evaluation.LockRequired ||
            (nextLockStartLocalTime is DateTime nextTarget
                ? !snapshot.Evaluation.LockRequired && (_lastDiagnosticTarget is not DateTime previousTarget ||
                    (nextTarget - previousTarget).Duration() > SameTargetTolerance)
                : _lastDiagnosticTarget is not null))
        {
            _lastDiagnosticTarget = nextLockStartLocalTime;
            _diagnostics.Record(FormattableString.Invariant(
                $"observation now={observedLocalTime:O} snapshot={snapshot.EvaluatedLocalTime:O} target={nextLockStartLocalTime:O} locked={snapshot.Evaluation.LockRequired} enabled30={settings.ThirtyMinutes} enabled10={settings.TenMinutes} enabled5={settings.FiveMinutes} enabled1={settings.OneMinute}"));
            if (nextLockStartLocalTime is DateTime plannedTarget && !snapshot.Evaluation.LockRequired)
            {
                foreach (int minutes in ReminderMinutes)
                {
                    _diagnostics.Record(FormattableString.Invariant(
                        $"planned minutes={minutes} threshold={plannedTarget.AddMinutes(-minutes):O} target={plannedTarget:O} enabled={settings.IsEnabled(minutes)} alreadyPast={plannedTarget.AddMinutes(-minutes) < observedLocalTime}"));
                }
            }
        }
        _lastLockRequired = snapshot.Evaluation.LockRequired;
        if (_lastObservedTimestamp is long lastTimestamp && observedTimestamp >= lastTimestamp &&
            _timeProvider.GetElapsedTime(lastTimestamp, observedTimestamp) > MaxReminderLateness)
        {
            _diagnostics.Record(FormattableString.Invariant(
                $"observation-gap elapsed={_timeProvider.GetElapsedTime(lastTimestamp, observedTimestamp)} now={observedLocalTime:O}"));
        }
        if (nextLockStartLocalTime is null ||
            snapshot.Evaluation.LockRequired ||
            nextLockStartLocalTime.Value <= observedLocalTime)
        {
            RememberObservation(observedLocalTime, observedTimestamp, settings);
            _resetPending = false;
            return null;
        }

        DateTime targetLocalTime = nextLockStartLocalTime.Value;
        if (HasClockChanged(observedLocalTime, observedTimestamp))
        {
            _diagnostics.Record(FormattableString.Invariant($"skipped reason=clock-change now={observedLocalTime:O} target={targetLocalTime:O}"));
            if (!IsSameTarget(targetLocalTime))
            {
                _observedTargetLocalTime = targetLocalTime;
                _observedReminderMinutes.Clear();
            }
            else
            {
                _observedTargetLocalTime = targetLocalTime;
            }

            RememberObservation(observedLocalTime, observedTimestamp, settings);
            _resetPending = false;
            return null;
        }

        if (!IsSameTarget(targetLocalTime))
        {
            bool hadPreviousObservation = _lastObservedLocalTime is not null;
            _observedTargetLocalTime = targetLocalTime;
            _observedReminderMinutes.Clear();
            RememberObservation(observedLocalTime, observedTimestamp, settings);
            _resetPending = false;
            return EvaluateInitialThreshold(
                targetLocalTime,
                observedLocalTime,
                settings,
                allowSmallLateness: hadPreviousObservation &&
                    snapshot.Evaluation.HasActiveEmergencyUnlock);
        }

        _observedTargetLocalTime = targetLocalTime;
        if (_resetPending ||
            _lastObservedLocalTime is not DateTime previousObservedLocalTime)
        {
            RememberObservation(observedLocalTime, observedTimestamp, settings);
            _resetPending = false;
            return null;
        }

        LockReminderSettings previousSettings = _lastSettings ?? settings;
        RememberObservation(observedLocalTime, observedTimestamp, settings);
        if (observedLocalTime <= previousObservedLocalTime)
        {
            return null;
        }

        int? closestCrossedMinutes = null;
        TimeSpan closestLateness = TimeSpan.Zero;
        foreach (int minutes in ReminderMinutes)
        {
            if (_observedReminderMinutes.Contains(minutes))
            {
                continue;
            }

            DateTime thresholdLocalTime = targetLocalTime.AddMinutes(-minutes);
            if (previousObservedLocalTime < thresholdLocalTime &&
                thresholdLocalTime <= observedLocalTime)
            {
                _observedReminderMinutes.Add(minutes);
                if (closestCrossedMinutes is null || minutes < closestCrossedMinutes.Value)
                {
                    closestCrossedMinutes = minutes;
                    closestLateness = observedLocalTime - thresholdLocalTime;
                }
            }
        }

        if (closestCrossedMinutes is null)
        {
            return null;
        }
        if (closestLateness > MaxReminderLateness ||
            !previousSettings.IsEnabled(closestCrossedMinutes.Value) ||
            !settings.IsEnabled(closestCrossedMinutes.Value))
        {
            string reason = closestLateness > MaxReminderLateness ? "observation-late" : "disabled";
            _diagnostics.Record(FormattableString.Invariant(
                $"skipped reason={reason} minutes={closestCrossedMinutes} lateness={closestLateness} target={targetLocalTime:O}"));
            return null;
        }

        _diagnostics.Record(FormattableString.Invariant(
            $"due minutes={closestCrossedMinutes} lateness={closestLateness} target={targetLocalTime:O}"));
        return closestCrossedMinutes.Value;
    }

    /// <summary>
    /// Starts the next observation from the following snapshot without replaying crossed reminders.
    /// </summary>
    public void ResetObservation()
    {
        _diagnostics.Record("observation-reset reason=clock-or-power-event");
        _lastObservedLocalTime = null;
        _resetPending = true;
    }

    private bool IsSameTarget(DateTime targetLocalTime) =>
        _observedTargetLocalTime is DateTime observedTargetLocalTime &&
        (targetLocalTime - observedTargetLocalTime).Duration() <= SameTargetTolerance;

    private int? EvaluateInitialThreshold(
        DateTime targetLocalTime,
        DateTime observedLocalTime,
        LockReminderSettings settings,
        bool allowSmallLateness)
    {
        foreach (int minutes in ReminderMinutes)
        {
            DateTime thresholdLocalTime = targetLocalTime.AddMinutes(-minutes);
            TimeSpan lateness = observedLocalTime - thresholdLocalTime;
            bool shouldPlay = allowSmallLateness
                ? lateness >= TimeSpan.Zero && lateness <= MaxReminderLateness
                : lateness == TimeSpan.Zero;
            if (shouldPlay)
            {
                _observedReminderMinutes.Add(minutes);
                _diagnostics.Record(FormattableString.Invariant(
                    $"initial-threshold minutes={minutes} enabled={settings.IsEnabled(minutes)} lateness={lateness} target={targetLocalTime:O}"));
                return settings.IsEnabled(minutes) ? minutes : null;
            }
            if (lateness > TimeSpan.Zero)
            {
                _diagnostics.Record(FormattableString.Invariant(
                    $"skipped reason=initial-observation-after-threshold minutes={minutes} lateness={lateness} target={targetLocalTime:O}"));
            }
        }

        return null;
    }

    private bool HasClockChanged(DateTime observedLocalTime, long observedTimestamp)
    {
        if (_lastObservedLocalTime is not DateTime previousLocalTime ||
            _lastObservedTimestamp is not long previousTimestamp)
        {
            return false;
        }

        if (observedTimestamp < previousTimestamp)
        {
            return true;
        }

        TimeSpan wallElapsed = observedLocalTime - previousLocalTime;
        if (wallElapsed < TimeSpan.Zero)
        {
            return true;
        }

        TimeSpan monotonicElapsed = _timeProvider.GetElapsedTime(
            previousTimestamp,
            observedTimestamp);
        return (wallElapsed - monotonicElapsed).Duration() > ClockChangeTolerance;
    }

    private void RememberObservation(
        DateTime observedLocalTime,
        long observedTimestamp,
        LockReminderSettings settings)
    {
        _lastObservedLocalTime = observedLocalTime;
        _lastObservedTimestamp = observedTimestamp;
        _lastSettings = settings;
    }
}
