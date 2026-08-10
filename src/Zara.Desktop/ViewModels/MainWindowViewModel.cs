using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly Func<bool, Task> _updateRestartSetting;
    private readonly AsyncCommand _toggleRestartSettingCommand;
    private readonly AsyncCommand _saveUsagePolicySettingsCommand;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly UsagePolicyRuntime? _usagePolicyRuntime;
    private bool _restartOnExitWhenUnlocked;
    private bool _isSettingsChangeAllowed = true;
    private int _emergencyDurationMinutes = EmergencyUnlockSettings.Default.DurationMinutes;
    private int _emergencySentenceCount = EmergencyUnlockSettings.Default.SentenceCount;
    private string _usagePolicyStatusMessage = "시간 규칙을 불러오는 중입니다.";
    private string _operationMessage = string.Empty;
    private int _disposed;

    /// <summary>
    /// Keeps the existing desktop composition path available until the App composes the time-rule
    /// runtime. Time-rule editing remains unavailable through this constructor.
    /// </summary>
    internal MainWindowViewModel(
        bool restartOnExitWhenUnlocked,
        Func<bool, Task> updateRestartSetting,
        Func<Task> requestLock)
    {
        _restartOnExitWhenUnlocked = restartOnExitWhenUnlocked;
        _updateRestartSetting =
            updateRestartSetting ?? throw new ArgumentNullException(nameof(updateRestartSetting));
        ArgumentNullException.ThrowIfNull(requestLock);
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
            ReportFailure,
            () => CanChangeSettings);
        _saveUsagePolicySettingsCommand = new AsyncCommand(
            SaveUsagePolicySettingsAsync,
            ReportFailure,
            () => CanChangeUsagePolicySettings);
        StartLockDemoCommand = new AsyncCommand(requestLock, ReportFailure);
    }

    /// <summary>
    /// Creates a desktop settings model connected to the initialized time-rule runtime.
    /// </summary>
    internal MainWindowViewModel(
        UsagePolicyRuntime usagePolicyRuntime,
        bool restartOnExitWhenUnlocked,
        Func<bool, Task> updateRestartSetting,
        Func<Task> requestLock)
        : this(restartOnExitWhenUnlocked, updateRestartSetting, requestLock)
    {
        _usagePolicyRuntime = usagePolicyRuntime ??
            throw new ArgumentNullException(nameof(usagePolicyRuntime));
        _usagePolicyRuntime.StateChanged += OnUsagePolicyStateChanged;
        ApplyUsagePolicySnapshot(_usagePolicyRuntime.CurrentSnapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the weekday editors shown on the usage-ban tab.</summary>
    public ObservableCollection<DailyUsageRestrictionViewModel> WeekdayRestrictions { get; }

    /// <summary>Gets the fixed duration values offered by the emergency-unlock form.</summary>
    public IReadOnlyList<int> EmergencyDurationMinuteOptions { get; } =
        Enumerable.Range(
            EmergencyUnlockSettings.MinimumDurationMinutes,
            EmergencyUnlockSettings.MaximumDurationMinutes -
                EmergencyUnlockSettings.MinimumDurationMinutes + 1).ToArray();

    /// <summary>Gets the fixed prompt-count values offered by the emergency-unlock form.</summary>
    public IReadOnlyList<int> EmergencySentenceCountOptions { get; } =
        Enumerable.Range(
            EmergencyUnlockSettings.MinimumSentenceCount,
            EmergencyUnlockSettings.MaximumSentenceCount -
                EmergencyUnlockSettings.MinimumSentenceCount + 1).ToArray();

    /// <summary>Gets the sortable reservation rows.</summary>
    public ObservableCollection<ReservationRowViewModel> Reservations { get; }

    /// <summary>Gets the view used by the reservation list and column-header sorting.</summary>
    public ICollectionView ReservationsView { get; }

    public ICommand ToggleRestartSettingCommand => _toggleRestartSettingCommand;

    /// <summary>Gets the command that persists weekday and emergency-unlock form values.</summary>
    public ICommand SaveUsagePolicySettingsCommand => _saveUsagePolicySettingsCommand;

    public ICommand StartLockDemoCommand { get; }

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

    /// <summary>Gets or sets the selected emergency-unlock duration in minutes.</summary>
    public int EmergencyDurationMinutes
    {
        get => _emergencyDurationMinutes;
        set => SetField(ref _emergencyDurationMinutes, value);
    }

    /// <summary>Gets or sets the selected emergency prompt count.</summary>
    public int EmergencySentenceCount
    {
        get => _emergencySentenceCount;
        set => SetField(ref _emergencySentenceCount, value);
    }

    /// <summary>Gets a read-only explanation of the current evaluated time-rule state.</summary>
    public string UsagePolicyStatusMessage
    {
        get => _usagePolicyStatusMessage;
        private set => SetField(ref _usagePolicyStatusMessage, value);
    }

    /// <summary>Gets the latest user-visible result of a settings command.</summary>
    public string OperationMessage
    {
        get => _operationMessage;
        private set => SetField(ref _operationMessage, value);
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

    /// <summary>
    /// Refreshes display-only past and active reservation state from the current local Windows time.
    /// </summary>
    internal void RefreshReservationPresentation() =>
        UpdateReservationPresentation(DateTime.Now, CanChangeSettings);

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
            OperationMessage = "정상 사용 시간의 자동 실행 설정을 저장했습니다.";
        }
        catch
        {
            OnPropertyChanged(nameof(RestartOnExitWhenUnlocked));
            throw;
        }
    }

    private async Task SaveUsagePolicySettingsAsync()
    {
        var emergencyUnlock = new EmergencyUnlockSettings(
            EmergencyDurationMinutes,
            EmergencySentenceCount);
        await RequireUsagePolicyRuntime()
            .UpdateSettingsAsync(BuildWeeklySchedule(), emergencyUnlock)
            .ConfigureAwait(true);
        OperationMessage = "사용 금지 시각과 긴급 해제 설정을 저장했습니다.";
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
        foreach (DailyUsageRestrictionViewModel editor in WeekdayRestrictions)
        {
            editor.Load(snapshot.Settings.WeeklySchedule.GetRestriction(editor.DayOfWeek));
        }

        EmergencyDurationMinutes = snapshot.Settings.EmergencyUnlock.DurationMinutes;
        EmergencySentenceCount = snapshot.Settings.EmergencyUnlock.SentenceCount;
        ReplaceReservationRows(snapshot.Settings.Reservations);
        UsagePolicyStatusMessage = GetUsagePolicyStatusMessage(snapshot);
        OnPropertyChanged(nameof(CanChangeSettings));
        OnPropertyChanged(nameof(CanChangeUsagePolicySettings));
        _toggleRestartSettingCommand.NotifyCanExecuteChanged();
        _saveUsagePolicySettingsCommand.NotifyCanExecuteChanged();
    }

    private void ReplaceReservationRows(IEnumerable<OutOfHoursReservation> reservations)
    {
        Reservations.Clear();
        foreach (OutOfHoursReservation reservation in reservations)
        {
            Reservations.Add(new ReservationRowViewModel(reservation));
        }

        UpdateReservationPresentation(DateTime.Now, CanChangeSettings);
        ReservationsView.Refresh();
    }

    private void UpdateReservationPresentation(DateTime localNow, bool isSettingsChangeAllowed)
    {
        foreach (ReservationRowViewModel reservation in Reservations)
        {
            reservation.UpdatePresentation(localNow, isSettingsChangeAllowed);
        }
    }

    private static string GetUsagePolicyStatusMessage(UsagePolicyRuntimeSnapshot snapshot)
    {
        UsagePolicyEvaluation evaluation = snapshot.Evaluation;
        if (!evaluation.IsWithinUsageBan)
        {
            return "현재는 사용 금지 시간이 아닙니다.";
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

    private UsagePolicyRuntime RequireUsagePolicyRuntime() => _usagePolicyRuntime ??
        throw new InvalidOperationException("The usage-policy runtime is not initialized.");

    private void ReportFailure(Exception exception)
    {
        OperationMessage = exception switch
        {
            UsagePolicySettingsLockedException => "사용 금지 시간에는 설정을 변경할 수 없습니다.",
            ArgumentException => exception.Message,
            _ => "설정을 저장하지 못했습니다. 잠시 후 다시 시도하세요.",
        };
    }

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
