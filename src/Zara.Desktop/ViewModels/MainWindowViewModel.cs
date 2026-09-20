using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using Zara.Application.UsagePolicy;
using Zara.Core.UsagePolicy;
using Zara.Desktop.Commands;

namespace Zara.Desktop.ViewModels;

/// <summary>
/// Exposes desktop shell actions and projects the time-rule runtime into editable WPF settings
/// controls. Product validation and persistence stay in <see cref="UsagePolicyRuntime"/>.
/// </summary>
internal sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    internal const string SavedButApplyFailedMessage =
        "설정은 저장했습니다. 현재 잠금 상태를 적용하지 못해 자동으로 다시 시도합니다.";

    private readonly Func<bool, Task> _updateRestartSetting;
    private readonly AsyncCommand _toggleRestartSettingCommand;
    private readonly AsyncCommand _saveWeeklyScheduleCommand;
    private readonly AsyncCommand _saveEmergencyUnlockSettingsCommand;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly UsagePolicyRuntime? _usagePolicyRuntime;
    private bool _restartOnExitWhenUnlocked;
    private bool _isSettingsChangeAllowed = true;
    private string _emergencyDurationMinutesText = EmergencyUnlockSettings.Default.DurationMinutes
        .ToString(CultureInfo.InvariantCulture);
    private string _emergencySentenceCountText = EmergencyUnlockSettings.Default.SentenceCount
        .ToString(CultureInfo.InvariantCulture);
    private string _usagePolicyStatusMessage = "시간 규칙을 불러오는 중입니다.";
    private WeeklyUsageRestrictionSchedule? _loadedWeeklySchedule;
    private EmergencyUnlockSettings? _loadedEmergencyUnlockSettings;
    private OutOfHoursReservation[] _loadedReservations = [];
    private int _disposed;

    /// <summary>
    /// Keeps the existing desktop composition path available until the App composes the time-rule
    /// runtime. Time-rule editing remains unavailable through this constructor.
    /// </summary>
    internal MainWindowViewModel(
        bool restartOnExitWhenUnlocked,
        Func<bool, Task> updateRestartSetting
#if DEBUG
        , Func<Task> requestLock,
        Func<Task> requestDevelopmentUnlock
#endif
        )
    {
        _restartOnExitWhenUnlocked = restartOnExitWhenUnlocked;
        _updateRestartSetting =
            updateRestartSetting ?? throw new ArgumentNullException(nameof(updateRestartSetting));
#if DEBUG
        ArgumentNullException.ThrowIfNull(requestLock);
        ArgumentNullException.ThrowIfNull(requestDevelopmentUnlock);
#endif

        _synchronizationContext = SynchronizationContext.Current;

        WeekdayRestrictions = new ObservableCollection<DailyUsageRestrictionViewModel>(
        [
            new(DayOfWeek.Monday, "월요일"),
            new(DayOfWeek.Tuesday, "화요일"),
            new(DayOfWeek.Wednesday, "수요일"),
            new(DayOfWeek.Thursday, "목요일"),
            new(DayOfWeek.Friday, "금요일"),
            new(DayOfWeek.Saturday, "토요일"),
            new(DayOfWeek.Sunday, "일요일"),
        ]);
        Reservations = new ObservableCollection<ReservationRowViewModel>();
        ReservationsView = CollectionViewSource.GetDefaultView(Reservations);
        ReservationsView.SortDescriptions.Add(
            new SortDescription(nameof(ReservationRowViewModel.Date), ListSortDirection.Ascending));
        ReservationsView.SortDescriptions.Add(
            new SortDescription(nameof(ReservationRowViewModel.StartTime), ListSortDirection.Ascending));

        _toggleRestartSettingCommand = new AsyncCommand(
            ToggleRestartSettingAsync,
            ReportSettingsFailure,
            () => CanChangeSettings);
        _saveWeeklyScheduleCommand = new AsyncCommand(
            SaveWeeklyScheduleAsync,
            ReportWeeklyScheduleFailure,
            () => CanChangeUsagePolicySettings);
        _saveEmergencyUnlockSettingsCommand = new AsyncCommand(
            SaveEmergencyUnlockSettingsAsync,
            ReportEmergencyUnlockSettingsFailure,
            () => CanChangeUsagePolicySettings);
#if DEBUG
        StartLockDemoCommand = new AsyncCommand(requestLock, ReportLockDemoFailure);
        DevelopmentUnlockCommand = new AsyncCommand(
            requestDevelopmentUnlock,
            ReportDevelopmentUnlockFailure);
#endif

    }

    /// <summary>
    /// Creates a desktop settings model connected to the initialized time-rule runtime.
    /// </summary>
    internal MainWindowViewModel(
        UsagePolicyRuntime usagePolicyRuntime,
        bool restartOnExitWhenUnlocked,
        Func<bool, Task> updateRestartSetting
#if DEBUG
        , Func<Task> requestLock,
        Func<Task> requestDevelopmentUnlock
#endif
        )
        : this(restartOnExitWhenUnlocked, updateRestartSetting
#if DEBUG
            , requestLock, requestDevelopmentUnlock
#endif
            )
    {
        _usagePolicyRuntime = usagePolicyRuntime ??
            throw new ArgumentNullException(nameof(usagePolicyRuntime));
        _usagePolicyRuntime.StateChanged += OnUsagePolicyStateChanged;
        ApplyUsagePolicySnapshot(_usagePolicyRuntime.CurrentSnapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised for each settings result so the owning window can show a fresh modal confirmation
    /// instead of leaving an ambiguous status line on screen.
    /// </summary>
    public event EventHandler<MainWindowNotificationEventArgs>? NotificationRequested;

    /// <summary>Gets the weekday editors shown on the usage-ban tab.</summary>
    public ObservableCollection<DailyUsageRestrictionViewModel> WeekdayRestrictions { get; }

    /// <summary>Gets the sortable reservation rows.</summary>
    public ObservableCollection<ReservationRowViewModel> Reservations { get; }

    /// <summary>Gets the view used by the reservation list and column-header sorting.</summary>
    public ICollectionView ReservationsView { get; }

    public ICommand ToggleRestartSettingCommand => _toggleRestartSettingCommand;

    /// <summary>Gets the command that persists only the weekday usage-ban schedule.</summary>
    public ICommand SaveWeeklyScheduleCommand => _saveWeeklyScheduleCommand;

    /// <summary>Gets the command that persists only the emergency-unlock settings.</summary>
    public ICommand SaveEmergencyUnlockSettingsCommand => _saveEmergencyUnlockSettingsCommand;

#if DEBUG
    public ICommand StartLockDemoCommand { get; }

    public ICommand DevelopmentUnlockCommand { get; }

#endif

    /// <summary>Gets whether the time-rule runtime was supplied by the App composition root.</summary>
    public bool HasUsagePolicyRuntime => _usagePolicyRuntime is not null;

    /// <summary>
    /// Gets whether any product setting may be changed at the current local time.
    /// </summary>
    public bool CanChangeSettings => HasUsagePolicyRuntime && _isSettingsChangeAllowed;

    /// <summary>
    /// Gets whether weekday, emergency-unlock, and reservation controls may be changed.
    /// </summary>
    public bool CanChangeUsagePolicySettings => CanChangeSettings;

    public bool RestartOnExitWhenUnlocked
    {
        get => _restartOnExitWhenUnlocked;
        private set => SetField(ref _restartOnExitWhenUnlocked, value);
    }

    /// <summary>
    /// Gets or sets the user-entered emergency-unlock duration. The value is validated only when
    /// the settings are saved so a partially typed number remains visible to the user.
    /// </summary>
    public string EmergencyDurationMinutesText
    {
        get => _emergencyDurationMinutesText;
        set => SetField(ref _emergencyDurationMinutesText, value);
    }

    /// <summary>
    /// Gets or sets the user-entered emergency prompt count. The value is validated only when the
    /// settings are saved so a partially typed number remains visible to the user.
    /// </summary>
    public string EmergencySentenceCountText
    {
        get => _emergencySentenceCountText;
        set => SetField(ref _emergencySentenceCountText, value);
    }

    /// <summary>Gets a read-only explanation of the current evaluated time-rule state.</summary>
    public string UsagePolicyStatusMessage
    {
        get => _usagePolicyStatusMessage;
        private set => SetField(ref _usagePolicyStatusMessage, value);
    }

    /// <summary>
    /// Converts a completed reservation dialog result into a Core reservation and delegates the
    /// add operation to the runtime.
    /// </summary>
    internal Task<ReservationChangeStatus> AddReservationAsync(
        ReservationDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var reservation = new OutOfHoursReservation(
            Guid.NewGuid(),
            draft.Date,
            draft.StartTime,
            draft.EndTime,
            draft.Memo);
        return RequireUsagePolicyRuntime().AddReservationAsync(reservation, cancellationToken);
    }

    /// <summary>
    /// Delegates reservation deletion to the runtime, which rejects a currently active reservation.
    /// </summary>
    internal Task<ReservationChangeStatus> RemoveReservationAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default) =>
        RequireUsagePolicyRuntime().RemoveReservationAsync(reservationId, cancellationToken);

    /// <summary>Stops observing runtime snapshots when the containing desktop window is disposed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_usagePolicyRuntime is not null)
        {
            _usagePolicyRuntime.StateChanged -= OnUsagePolicyStateChanged;
        }

        GC.SuppressFinalize(this);
    }

    private async Task ToggleRestartSettingAsync()
    {
        bool requestedValue = !RestartOnExitWhenUnlocked;

        // A CheckBox toggles before its command runs. Re-project the acknowledged value while the
        // durable setting and supervision lease are being updated.
        OnPropertyChanged(nameof(RestartOnExitWhenUnlocked));

        try
        {
            await _updateRestartSetting(requestedValue).ConfigureAwait(true);
            RestartOnExitWhenUnlocked = requestedValue;
            RequestNotification(
                "정상 사용 시간의 자동 실행 설정을 저장했습니다.",
                isError: false);
        }
        catch
        {
            OnPropertyChanged(nameof(RestartOnExitWhenUnlocked));
            throw;
        }
    }

    /// <summary>Persists the weekday form without reading or changing the emergency tab.</summary>
    internal async Task SaveWeeklyScheduleAsync()
    {
        await RequireUsagePolicyRuntime()
            .UpdateWeeklyScheduleAsync(BuildWeeklySchedule())
            .ConfigureAwait(true);
        RequestNotification(
            "사용 금지 시각",
            "사용 금지 시각을 저장했습니다.",
            isError: false);
    }

    /// <summary>Persists the emergency-unlock form without reading or changing the weekday tab.</summary>
    internal async Task SaveEmergencyUnlockSettingsAsync()
    {
        var emergencyUnlock = new EmergencyUnlockSettings(
            ParseEmergencySetting(
                EmergencyDurationMinutesText,
                "긴급 해제 유지 시간",
                EmergencyUnlockSettings.MinimumDurationMinutes,
                EmergencyUnlockSettings.MaximumDurationMinutes),
            ParseEmergencySetting(
                EmergencySentenceCountText,
                "긴급 해제 문장 수",
                EmergencyUnlockSettings.MinimumSentenceCount,
                EmergencyUnlockSettings.MaximumSentenceCount));
        await RequireUsagePolicyRuntime()
            .UpdateEmergencyUnlockSettingsAsync(emergencyUnlock)
            .ConfigureAwait(true);
        RequestNotification(
            "긴급 해제",
            "긴급 해제 설정을 저장했습니다.",
            isError: false);
    }

    /// <summary>Discards weekday edits and restores the latest settings held by the runtime.</summary>
    internal void ResetWeeklyScheduleEdits()
    {
        if (_usagePolicyRuntime is null)
        {
            return;
        }

        WeeklyUsageRestrictionSchedule schedule =
            _usagePolicyRuntime.CurrentSnapshot.Settings.WeeklySchedule;
        foreach (DailyUsageRestrictionViewModel editor in WeekdayRestrictions)
        {
            editor.Load(schedule.GetRestriction(editor.DayOfWeek));
        }

        _loadedWeeklySchedule = schedule;
    }

    /// <summary>Discards emergency-tab edits and restores the latest runtime settings.</summary>
    internal void ResetEmergencyUnlockEdits()
    {
        if (_usagePolicyRuntime is null)
        {
            return;
        }

        EmergencyUnlockSettings emergencyUnlock =
            _usagePolicyRuntime.CurrentSnapshot.Settings.EmergencyUnlock;
        EmergencyDurationMinutesText = emergencyUnlock.DurationMinutes
            .ToString(CultureInfo.InvariantCulture);
        EmergencySentenceCountText = emergencyUnlock.SentenceCount
            .ToString(CultureInfo.InvariantCulture);
        _loadedEmergencyUnlockSettings = emergencyUnlock;
    }

    /// <summary>Discards all unsaved time-rule form edits before the window is hidden.</summary>
    internal void ResetUsagePolicyEdits()
    {
        ResetWeeklyScheduleEdits();
        ResetEmergencyUnlockEdits();
    }

    private WeeklyUsageRestrictionSchedule BuildWeeklySchedule()
    {
        WeeklyUsageRestrictionSchedule schedule = WeeklyUsageRestrictionSchedule.Default;
        foreach (DailyUsageRestrictionViewModel editor in WeekdayRestrictions)
        {
            schedule = schedule.WithRestriction(editor.DayOfWeek, editor.ToRestriction());
        }

        return schedule;
    }

    private void OnUsagePolicyStateChanged(
        object? sender,
        UsagePolicyRuntimeSnapshot snapshot)
    {
        if (_synchronizationContext is not null &&
            !ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
        {
            _synchronizationContext.Post(
                _ => ApplyUsagePolicySnapshot(snapshot),
                state: null);
            return;
        }

        ApplyUsagePolicySnapshot(snapshot);
    }

    private void ApplyUsagePolicySnapshot(UsagePolicyRuntimeSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _isSettingsChangeAllowed = snapshot.Evaluation.IsSettingsChangeAllowed;
        if (!Equals(_loadedWeeklySchedule, snapshot.Settings.WeeklySchedule))
        {
            foreach (DailyUsageRestrictionViewModel editor in WeekdayRestrictions)
            {
                editor.Load(snapshot.Settings.WeeklySchedule.GetRestriction(editor.DayOfWeek));
            }

            _loadedWeeklySchedule = snapshot.Settings.WeeklySchedule;
        }

        if (!Equals(_loadedEmergencyUnlockSettings, snapshot.Settings.EmergencyUnlock))
        {
            EmergencyDurationMinutesText = snapshot.Settings.EmergencyUnlock.DurationMinutes
                .ToString(CultureInfo.InvariantCulture);
            EmergencySentenceCountText = snapshot.Settings.EmergencyUnlock.SentenceCount
                .ToString(CultureInfo.InvariantCulture);
            _loadedEmergencyUnlockSettings = snapshot.Settings.EmergencyUnlock;
        }

        if (!_loadedReservations.SequenceEqual(snapshot.Settings.Reservations))
        {
            ReplaceReservationRows(snapshot.Settings.Reservations);
            _loadedReservations = snapshot.Settings.Reservations.ToArray();
        }

        UpdateReservationPresentation(snapshot.EvaluatedLocalTime);
        UsagePolicyStatusMessage = GetUsagePolicyStatusMessage(snapshot);
        OnPropertyChanged(nameof(CanChangeSettings));
        OnPropertyChanged(nameof(CanChangeUsagePolicySettings));
        _toggleRestartSettingCommand.NotifyCanExecuteChanged();
        _saveWeeklyScheduleCommand.NotifyCanExecuteChanged();
        _saveEmergencyUnlockSettingsCommand.NotifyCanExecuteChanged();
    }

    private void ReplaceReservationRows(IEnumerable<OutOfHoursReservation> reservations)
    {
        Reservations.Clear();
        foreach (OutOfHoursReservation reservation in reservations)
        {
            Reservations.Add(new ReservationRowViewModel(reservation));
        }

        ReservationsView.Refresh();
    }

    private void UpdateReservationPresentation(DateTime localNow)
    {
        foreach (ReservationRowViewModel reservation in Reservations)
        {
            reservation.UpdatePresentation(localNow);
        }
    }

    private static string GetUsagePolicyStatusMessage(UsagePolicyRuntimeSnapshot snapshot)
    {
        UsagePolicyEvaluation evaluation = snapshot.Evaluation;
        if (!evaluation.IsWithinUsageBan)
        {
            DateTime? nextLockStart = snapshot.NextLockStartLocalTime;
            if (nextLockStart is null)
            {
                return "지금은 설정된 사용 금지 시각이 없습니다.";
            }

            long remainingSeconds = Math.Max(
                0,
                (long)Math.Ceiling(
                    (nextLockStart.Value - snapshot.EvaluatedLocalTime).TotalSeconds));
            long remainingHours = remainingSeconds / 3600;
            long remainingMinutes = remainingSeconds % 3600 / 60;
            long remainingSecondsPart = remainingSeconds % 60;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"다음 잠금까지 {remainingHours:00}:{remainingMinutes:00}:{remainingSecondsPart:00} 남았습니다.");
        }

        if (evaluation.HasActiveEmergencyUnlock)
        {
            return "긴급 해제가 적용 중입니다. 사용 금지 시간이라 설정은 바꿀 수 없습니다.";
        }

        if (evaluation.HasActiveReservation)
        {
            return "시간 외 사용 예약이 적용 중입니다. 사용 금지 시간이라 설정은 바꿀 수 없습니다.";
        }

        return evaluation.LockRequired
            ? "현재는 사용 금지 시간입니다. 설정을 바꿀 수 없습니다."
            : "현재 설정 변경이 제한되어 있습니다.";
    }

    private static int ParseEmergencySetting(
        string value,
        string displayName,
        int minimum,
        int maximum)
    {
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsedValue) ||
            parsedValue < minimum ||
            parsedValue > maximum)
        {
            throw new ArgumentException(
                $"{displayName}은 {minimum}부터 {maximum}까지의 정수로 입력하세요.");
        }

        return parsedValue;
    }

    private UsagePolicyRuntime RequireUsagePolicyRuntime() => _usagePolicyRuntime ??
        throw new InvalidOperationException("The usage-policy runtime is not initialized.");

    private void ReportSettingsFailure(Exception exception)
    {
        string message = GetSettingsFailureMessage(
            exception,
            "설정을 저장하지 못했습니다. 잠시 후 다시 시도하세요.");
        RequestNotification("설정 저장", message, isError: true);
    }

    private void ReportWeeklyScheduleFailure(Exception exception)
    {
        string message = GetSettingsFailureMessage(
            exception,
            "사용 금지 시각을 저장하지 못했습니다. 잠시 후 다시 시도하세요.");
        RequestNotification("사용 금지 시각", message, isError: true);
    }

    private void ReportEmergencyUnlockSettingsFailure(Exception exception)
    {
        string message = GetSettingsFailureMessage(
            exception,
            "긴급 해제 설정을 저장하지 못했습니다. 잠시 후 다시 시도하세요.");
        RequestNotification("긴급 해제", message, isError: true);
    }

    private static string GetSettingsFailureMessage(
        Exception exception,
        string unexpectedFailureMessage)
    {
        return exception switch
        {
            UsagePolicySettingsSavedButApplyFailedException =>
                SavedButApplyFailedMessage,
            UsagePolicySettingsLockedException => "사용 금지 시간에는 설정을 변경할 수 없습니다.",
            ArgumentException => exception.Message,
            _ => unexpectedFailureMessage,
        };
    }

#if DEBUG
    private void ReportLockDemoFailure(Exception _) =>
        RequestNotification(
            "잠금 화면 시연",
            "잠금 화면을 열지 못했습니다. 잠시 후 다시 시도하세요.",
            isError: true);

    private void ReportDevelopmentUnlockFailure(Exception _) =>
        RequestNotification(
            "잠금 해제(개발용)",
            "잠금을 해제하지 못했습니다. 잠시 후 다시 시도하세요.",
            isError: true);

#endif

    private void RequestNotification(string message, bool isError) =>
        RequestNotification("설정 저장", message, isError);

    private void RequestNotification(string title, string message, bool isError) =>
        NotificationRequested?.Invoke(
            this,
            new MainWindowNotificationEventArgs(title, message, isError));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Describes one user-visible modal result from the main window.</summary>
internal sealed class MainWindowNotificationEventArgs : EventArgs
{
    /// <summary>Creates a notification with user-visible Korean text and severity.</summary>
    internal MainWindowNotificationEventArgs(string title, string message, bool isError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Title = title;
        Message = message;
        IsError = isError;
    }

    /// <summary>Gets the user-visible dialog title.</summary>
    public string Title { get; }

    /// <summary>Gets the user-visible result text.</summary>
    public string Message { get; }

    /// <summary>Gets whether the result should use an error icon.</summary>
    public bool IsError { get; }
}
