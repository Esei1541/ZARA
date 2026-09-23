using System.Diagnostics;
using Zara.Core.UsagePolicy;

namespace Zara.Application.UsagePolicy;

/// <summary>
/// Serializes persisted time-rule settings, in-memory emergency unlocks, and the lock transition
/// sent to the existing overlay and restart-continuity path.
/// </summary>
/// <remarks>
/// Windows local time is read only through the injected <see cref="TimeProvider"/>. Emergency
/// durations use its monotonic timestamp APIs, so manually changing the wall clock can immediately
/// affect schedules and reservations without changing an unlock's remaining elapsed time.
/// </remarks>
public sealed class UsagePolicyRuntime : IDisposable
{
    private readonly IUsagePolicySettingsStore _store;
    private readonly IUsagePolicyLockPort _lockPort;
    private readonly IEmergencyUnlockPromptCatalog _promptCatalog;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private UsagePolicyRuntimeSnapshot _currentSnapshot;
    private long? _emergencyUnlockStartedTimestamp;
    private EmergencyUnlockChallenge? _pendingChallenge;
    private bool? _lastAppliedLockRequirement;
    private bool? _lastAppliedRestartLockRequirement;
    private int _disposed;

    /// <summary>
    /// Initializes a runtime with product defaults until persisted settings are loaded.
    /// </summary>
    public UsagePolicyRuntime(
        IUsagePolicySettingsStore store,
        IUsagePolicyLockPort lockPort,
        IEmergencyUnlockPromptCatalog promptCatalog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lockPort);
        ArgumentNullException.ThrowIfNull(promptCatalog);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _store = store;
        _lockPort = lockPort;
        _promptCatalog = promptCatalog;
        _timeProvider = timeProvider;
        _currentSnapshot = CreateSnapshot(UsagePolicySettings.Default);
    }

    /// <summary>
    /// Raised after a serialized operation has produced a new observable policy state.
    /// </summary>
    public event EventHandler<UsagePolicyRuntimeSnapshot>? StateChanged;

    /// <summary>
    /// Gets the latest immutable state snapshot.
    /// </summary>
    public UsagePolicyRuntimeSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

    /// <summary>
    /// Loads persisted settings, discarding any emergency state from an earlier app process, and
    /// applies the initial lock decision.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async () =>
            {
                UsagePolicySettings settings = await _store.LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
                _emergencyUnlockStartedTimestamp = null;
                _pendingChallenge = null;
                await SetSettingsAndApplyAsync(settings, forceLockApply: true, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// Re-evaluates restrictions after elapsed time, Windows time changes, or a power-state resume.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            () => SetSettingsAndApplyAsync(
                CurrentSnapshot.Settings,
                forceLockApply: false,
                cancellationToken),
            cancellationToken);

    /// <summary>
    /// Serializes confirmed exit with policy changes and checks time again after cleanup.
    /// A null final change preserves independent manual-lock and development-safety intentions.
    /// Callbacks must not re-enter this runtime; exit must initiate shutdown before returning.
    /// </summary>
    internal Task ExecuteExitAsync(
        Func<CancellationToken, Task> prepare,
        Func<bool?, CancellationToken, Task> exit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(exit);
        return ExecuteAsync(
            async () =>
            {
                await SetSettingsAndApplyAsync(
                    CurrentSnapshot.Settings,
                    forceLockApply: false,
                    cancellationToken).ConfigureAwait(false);
                bool initialRequirement = CurrentSnapshot.Evaluation.LockRequiredAfterRestart;
                await prepare(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                bool latestRequirement = CreateSnapshot(CurrentSnapshot.Settings)
                    .Evaluation.LockRequiredAfterRestart;
                await exit(
                    initialRequirement == latestRequirement ? null : latestRequirement,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Clears an in-memory emergency unlock and pending prompt when sleep, hibernation, or an
    /// equivalent lifecycle boundary begins.
    /// </summary>
    public Task ClearEmergencyUnlockAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async () =>
            {
                _emergencyUnlockStartedTimestamp = null;
                _pendingChallenge = null;
                await SetSettingsAndApplyAsync(
                    CurrentSnapshot.Settings,
                    forceLockApply: false,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// Replaces weekday and emergency-unlock settings after verifying that the base restriction is
    /// not active.
    /// </summary>
    public Task UpdateSettingsAsync(
        WeeklyUsageRestrictionSchedule weeklySchedule,
        EmergencyUnlockSettings emergencyUnlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(weeklySchedule);
        ArgumentNullException.ThrowIfNull(emergencyUnlock);

        return ExecuteAsync(
            async () =>
            {
                ThrowIfSettingsChangeBlocked();
                UsagePolicySettings current = CurrentSnapshot.Settings;
                var updated = new UsagePolicySettings(
                    weeklySchedule,
                    emergencyUnlock,
                    current.Reservations);
                await PersistAndApplyAsync(updated, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Replaces only the weekday schedule while preserving the latest emergency-unlock settings
    /// and reservations serialized by this runtime.
    /// </summary>
    public Task UpdateWeeklyScheduleAsync(
        WeeklyUsageRestrictionSchedule weeklySchedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(weeklySchedule);

        return ExecuteAsync(
            async () =>
            {
                ThrowIfSettingsChangeBlocked();
                UsagePolicySettings current = CurrentSnapshot.Settings;
                var updated = new UsagePolicySettings(
                    weeklySchedule,
                    current.EmergencyUnlock,
                    current.Reservations);
                await PersistAndApplyAsync(updated, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

#if DEBUG
    /// <summary>
    /// Disables every weekday restriction for development safety, even while settings are locked,
    /// preserving the configured times, emergency-unlock settings, and reservations.
    /// </summary>
    public Task DisableWeeklyScheduleForDevelopmentAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async () =>
            {
                UsagePolicySettings current = CurrentSnapshot.Settings;
                WeeklyUsageRestrictionSchedule schedule = current.WeeklySchedule;
                foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
                {
                    DailyUsageRestriction restriction = schedule.GetRestriction(day);
                    schedule = schedule.WithRestriction(
                        day,
                        new DailyUsageRestriction(
                            isEnabled: false,
                            restriction.StartTime,
                            restriction.ReleaseTime));
                }

                await PersistAndApplyAsync(current.WithWeeklySchedule(schedule), cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

#endif

    /// <summary>
    /// Replaces only the emergency-unlock settings while preserving the latest weekday schedule
    /// and reservations serialized by this runtime.
    /// </summary>
    public Task UpdateEmergencyUnlockSettingsAsync(
        EmergencyUnlockSettings emergencyUnlock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emergencyUnlock);

        return ExecuteAsync(
            async () =>
            {
                ThrowIfSettingsChangeBlocked();
                UsagePolicySettings current = CurrentSnapshot.Settings;
                var updated = new UsagePolicySettings(
                    current.WeeklySchedule,
                    emergencyUnlock,
                    current.Reservations);
                await PersistAndApplyAsync(updated, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Adds a reservation when settings are currently mutable.
    /// </summary>
    public Task<ReservationChangeStatus> AddReservationAsync(
        OutOfHoursReservation reservation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        return ExecuteAsync(
            async () =>
            {
                ThrowIfSettingsChangeBlocked();
                ReservationChangeResult result = UsagePolicyEvaluator.TryAddReservation(
                    CurrentSnapshot.Settings,
                    reservation);
                if (result.Succeeded)
                {
                    await PersistAndApplyAsync(result.Settings, cancellationToken).ConfigureAwait(false);
                }

                return result.Status;
            },
            cancellationToken);
    }

    /// <summary>
    /// Removes a reservation even while settings are locked, then immediately re-evaluates the lock.
    /// </summary>
    public Task<ReservationChangeStatus> RemoveReservationAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async () =>
            {
                ReservationChangeResult result = UsagePolicyEvaluator.TryRemoveReservation(
                    CurrentSnapshot.Settings,
                    reservationId);
                if (result.Succeeded)
                {
                    await PersistAndApplyAsync(result.Settings, cancellationToken).ConfigureAwait(false);
                }

                return result.Status;
            },
            cancellationToken);

    /// <summary>
    /// Starts an immediate zero-prompt unlock or returns the unique prompts that must be entered.
    /// </summary>
    public Task<EmergencyUnlockStartResult> StartEmergencyUnlockAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async () =>
            {
                await SetSettingsAndApplyAsync(
                    CurrentSnapshot.Settings,
                    forceLockApply: false,
                    cancellationToken).ConfigureAwait(false);
                UsagePolicyRuntimeSnapshot current = CurrentSnapshot;
                if (!current.Evaluation.IsWithinUsageBan)
                {
                    throw new InvalidOperationException(
                        "Emergency unlock is only available during a configured usage restriction.");
                }

                if (_emergencyUnlockStartedTimestamp is not null)
                {
                    throw new InvalidOperationException("An emergency unlock is already active.");
                }

                if (_pendingChallenge is not null)
                {
                    return new EmergencyUnlockStartResult(false, _pendingChallenge);
                }

                int sentenceCount = current.Settings.EmergencyUnlock.SentenceCount;
                if (sentenceCount == 0)
                {
                    _emergencyUnlockStartedTimestamp = _timeProvider.GetTimestamp();
                    await SetSettingsAndApplyAsync(
                        current.Settings,
                        forceLockApply: false,
                        cancellationToken).ConfigureAwait(false);
                    return new EmergencyUnlockStartResult(true, null);
                }

                IReadOnlyList<string> sentences = await _promptCatalog
                    .SelectDistinctAsync(sentenceCount, cancellationToken)
                    .ConfigureAwait(false);
                if (sentences.Count != sentenceCount ||
                    sentences.Any(string.IsNullOrWhiteSpace) ||
                    sentences.Distinct(StringComparer.Ordinal).Count() != sentenceCount)
                {
                    throw new InvalidOperationException(
                        "The emergency prompt catalog did not return the requested distinct prompts.");
                }

                _pendingChallenge = new EmergencyUnlockChallenge(sentences.ToArray());
                PublishSnapshot(CreateSnapshot(current.Settings));
                return new EmergencyUnlockStartResult(false, _pendingChallenge);
            },
            cancellationToken);

    /// <summary>
    /// Compares entered prompt text with the pending sequence and starts the timed unlock only on
    /// an exact match.
    /// </summary>
    /// <returns><see langword="true"/> when the timed emergency unlock started.</returns>
    public Task<bool> CompleteEmergencyUnlockAsync(
        string enteredText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enteredText);

        return ExecuteAsync(
            async () =>
            {
                EmergencyUnlockChallenge challenge = _pendingChallenge ??
                    throw new InvalidOperationException("No emergency unlock challenge is pending.");
                if (!EmergencyUnlockInputMatcher.IsExactMatch(challenge, enteredText))
                {
                    return false;
                }

                _pendingChallenge = null;
                _emergencyUnlockStartedTimestamp = _timeProvider.GetTimestamp();
                await SetSettingsAndApplyAsync(
                    CurrentSnapshot.Settings,
                    forceLockApply: false,
                    cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Releases the serialized gate. The injected store remains owned by its composition root.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        GC.SuppressFinalize(this);
    }

    private async Task PersistAndApplyAsync(
        UsagePolicySettings settings,
        CancellationToken cancellationToken)
    {
        settings = UsagePolicyEvaluator.RemoveExpiredReservations(settings, GetCurrentLocalTime());
        await _store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        try
        {
            await SetSettingsAndApplyAsync(settings, forceLockApply: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The new settings are already durable. Keep them in memory so the next refresh retries
            // the lock change using the persisted policy instead of continuing with an old snapshot.
            PublishSnapshotIfChanged(settings);
            throw new UsagePolicySettingsSavedButApplyFailedException(exception);
        }
    }

    private async Task SetSettingsAndApplyAsync(
        UsagePolicySettings settings,
        bool forceLockApply,
        CancellationToken cancellationToken)
    {
        DateTime localNow = GetCurrentLocalTime();
        bool emergencyUnlockActive = IsEmergencyUnlockActive(settings.EmergencyUnlock);
        UsagePolicyEvaluation evaluation = UsagePolicyEvaluator.Evaluate(
            settings,
            localNow,
            emergencyUnlockActive);
        bool lockRequirementChanged = forceLockApply ||
            _lastAppliedLockRequirement != evaluation.LockRequired ||
            _lastAppliedRestartLockRequirement != evaluation.LockRequiredAfterRestart;
        if (lockRequirementChanged)
        {
            await _lockPort
                .ApplyPolicyLockRequirementAsync(
                    evaluation.LockRequired,
                    evaluation.LockRequiredAfterRestart,
                    cancellationToken)
                .ConfigureAwait(false);
            _lastAppliedLockRequirement = evaluation.LockRequired;
            _lastAppliedRestartLockRequirement = evaluation.LockRequiredAfterRestart;
        }

        UsagePolicySettings remainingSettings = UsagePolicyEvaluator.RemoveExpiredReservations(
            settings,
            localNow);
        if (!ReferenceEquals(settings, remainingSettings))
        {
            try
            {
                // Apply the lock first: cleanup I/O must not delay or prevent an expired
                // reservation from restoring the base restriction.
                await _store.SaveAsync(remainingSettings, cancellationToken).ConfigureAwait(false);
                settings = remainingSettings;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Retain the loaded settings so a later refresh retries cleanup, including when
                // the app starts with an expired reservation and its settings cannot be written.
                Trace.TraceError("Expired reservation cleanup could not be saved: {0}", exception);
            }
        }

        PublishSnapshotIfChanged(settings, evaluation, localNow);
    }

    private UsagePolicyRuntimeSnapshot CreateSnapshot(UsagePolicySettings settings)
    {
        DateTime localNow = GetCurrentLocalTime();
        bool emergencyUnlockActive = IsEmergencyUnlockActive(settings.EmergencyUnlock);
        DateTime? emergencyUnlockEndLocalTime = GetEmergencyUnlockEndLocalTime(
            settings.EmergencyUnlock,
            emergencyUnlockActive,
            localNow);
        return new UsagePolicyRuntimeSnapshot(
            settings,
            UsagePolicyEvaluator.Evaluate(settings, localNow, emergencyUnlockActive),
            localNow,
            _pendingChallenge is not null,
            emergencyUnlockEndLocalTime,
            UsagePolicyEvaluator.FindNextLockStart(
                settings,
                localNow,
                emergencyUnlockEndLocalTime));
    }

    private void PublishSnapshotIfChanged(
        UsagePolicySettings settings,
        UsagePolicyEvaluation? evaluation = null,
        DateTime? evaluatedLocalTime = null)
    {
        UsagePolicyRuntimeSnapshot nextSnapshot = evaluation is null
            ? CreateSnapshot(settings)
            : CreateSnapshot(settings, evaluation, evaluatedLocalTime ?? GetCurrentLocalTime());
        if (!Equals(CurrentSnapshot, nextSnapshot))
        {
            PublishSnapshot(nextSnapshot);
        }
    }

    private UsagePolicyRuntimeSnapshot CreateSnapshot(
        UsagePolicySettings settings,
        UsagePolicyEvaluation evaluation,
        DateTime localNow)
    {
        DateTime? emergencyUnlockEndLocalTime = GetEmergencyUnlockEndLocalTime(
            settings.EmergencyUnlock,
            evaluation.HasActiveEmergencyUnlock,
            localNow);
        return new UsagePolicyRuntimeSnapshot(
            settings,
            evaluation,
            localNow,
            _pendingChallenge is not null,
            emergencyUnlockEndLocalTime,
            UsagePolicyEvaluator.FindNextLockStart(
                settings,
                localNow,
                emergencyUnlockEndLocalTime));
    }

    private bool IsEmergencyUnlockActive(EmergencyUnlockSettings settings)
    {
        if (_emergencyUnlockStartedTimestamp is not long startedTimestamp)
        {
            return false;
        }

        TimeSpan elapsed = _timeProvider.GetElapsedTime(startedTimestamp);
        if (elapsed < TimeSpan.FromMinutes(settings.DurationMinutes))
        {
            return true;
        }

        _emergencyUnlockStartedTimestamp = null;
        return false;
    }

    private DateTime? GetEmergencyUnlockEndLocalTime(
        EmergencyUnlockSettings settings,
        bool emergencyUnlockActive,
        DateTime localNow)
    {
        if (!emergencyUnlockActive ||
            _emergencyUnlockStartedTimestamp is not long startedTimestamp)
        {
            return null;
        }

        TimeSpan remaining = TimeSpan.FromMinutes(settings.DurationMinutes) -
            _timeProvider.GetElapsedTime(startedTimestamp);
        return remaining > TimeSpan.Zero
            ? localNow.Add(remaining)
            : null;
    }

    private DateTime GetCurrentLocalTime() => _timeProvider.GetLocalNow().DateTime;

    private void ThrowIfSettingsChangeBlocked()
    {
        if (!CreateSnapshot(CurrentSnapshot.Settings).Evaluation.IsSettingsChangeAllowed)
        {
            throw new UsagePolicySettingsLockedException();
        }
    }

    private void PublishSnapshot(UsagePolicyRuntimeSnapshot snapshot)
    {
        Volatile.Write(ref _currentSnapshot, snapshot);
        StateChanged?.Invoke(this, snapshot);
    }

    private async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await operation().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
