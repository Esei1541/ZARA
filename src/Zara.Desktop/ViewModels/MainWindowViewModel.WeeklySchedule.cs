using System.Globalization;
using System.Windows.Input;
using Zara.Core.UsagePolicy;
using Zara.Desktop.Commands;

namespace Zara.Desktop.ViewModels;

internal sealed partial class MainWindowViewModel
{
    private readonly AsyncCommand _revertWeekdayCommand;
    private readonly AsyncCommand _discardWeeklyScheduleCommand;
    private readonly AsyncCommand _confirmWeeklyScheduleCommand;
    private DailyUsageRestrictionViewModel _selectedWeekday;
    private WeeklyUsageRestrictionSchedule? _pendingWeeklySchedule;
    private bool _loadingWeeklySchedule;
    private bool _isSavingWeeklySchedule;
    private bool _willLockImmediately;
    private string _weeklyScheduleImpact = string.Empty;
    private string _weeklyScheduleValidationMessage = string.Empty;

    public ICommand RevertWeekdayCommand => _revertWeekdayCommand;
    public ICommand DiscardWeeklyScheduleCommand => _discardWeeklyScheduleCommand;
    public ICommand ConfirmWeeklyScheduleCommand => _confirmWeeklyScheduleCommand;
    public ICommand CancelWeeklyScheduleConfirmationCommand { get; }

    public DailyUsageRestrictionViewModel SelectedWeekday
    {
        get => _selectedWeekday;
        set
        {
            if (value is not null && SetField(ref _selectedWeekday, value))
            {
                _revertWeekdayCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanEditWeeklySchedule => CanChangeUsagePolicySettings && !_isSavingWeeklySchedule;
    public bool HasWeeklyScheduleChanges => WeekdayRestrictions.Any(day => day.HasChanges);
    public bool IsWeeklyScheduleConfirmationVisible => _pendingWeeklySchedule is not null;
    public bool WillLockImmediately => _willLockImmediately;
    public string WeeklyScheduleImpact => _weeklyScheduleImpact;
    public string WeeklyScheduleValidationMessage => _weeklyScheduleValidationMessage;
    public string WeeklyScheduleChangeStatus => HasWeeklyScheduleChanges
        ? $"{WeekdayRestrictions.Count(day => day.HasChanges)}개 요일 변경 · 저장 전"
        : "모든 일정 저장됨";

    internal Task ConfirmWeeklyScheduleAsync() => _pendingWeeklySchedule is { } candidate
        ? SaveWeeklyScheduleCandidateAsync(candidate, immediateLockConfirmed: true)
        : Task.CompletedTask;

    private async Task SaveWeeklyScheduleCandidateAsync(
        WeeklyUsageRestrictionSchedule candidate,
        bool immediateLockConfirmed)
    {
        if (_isSavingWeeklySchedule)
        {
            return;
        }

        _isSavingWeeklySchedule = true;
        UpdateWeeklyScheduleCommands();
        try
        {
            bool saved = await RequireUsagePolicyRuntime().TryUpdateWeeklyScheduleAsync(
                candidate, immediateLockConfirmed).ConfigureAwait(true);
            if (!saved)
            {
                _pendingWeeklySchedule = candidate;
                OnPropertyChanged(nameof(IsWeeklyScheduleConfirmationVisible));
                return;
            }

            ClearWeeklyScheduleConfirmation();
            ResetWeeklyScheduleEdits();
            RequestNotification("사용 금지 시간대", "사용 금지 시간대를 저장했습니다.", isError: false);
        }
        finally
        {
            _isSavingWeeklySchedule = false;
            UpdateWeeklySchedulePresentation();
        }
    }

    private void OnWeeklyScheduleEdited(object? sender, EventArgs e)
    {
        if (!_loadingWeeklySchedule)
        {
            ClearWeeklyScheduleConfirmation();
            UpdateWeeklySchedulePresentation();
        }
    }

    private void LoadWeeklySchedule(WeeklyUsageRestrictionSchedule schedule)
    {
        _loadingWeeklySchedule = true;
        try
        {
            foreach (DailyUsageRestrictionViewModel editor in WeekdayRestrictions)
            {
                editor.Load(schedule.GetRestriction(editor.DayOfWeek));
            }

            _loadedWeeklySchedule = schedule;
        }
        finally
        {
            _loadingWeeklySchedule = false;
        }
    }

    private void ClearWeeklyScheduleConfirmation()
    {
        _pendingWeeklySchedule = null;
        OnPropertyChanged(nameof(IsWeeklyScheduleConfirmationVisible));
        _confirmWeeklyScheduleCommand?.NotifyCanExecuteChanged();
    }

    private void UpdateWeeklySchedulePresentation()
    {
        string validation = string.Empty;
        string impact = string.Empty;
        bool immediate = false;
        try
        {
            WeeklyUsageRestrictionSchedule candidate = BuildWeeklySchedule();
            if (_usagePolicyRuntime is not null)
            {
                var preview = _usagePolicyRuntime.PreviewWeeklySchedule(candidate);
                immediate = preview.Evaluation.LockRequired;
                impact = immediate ? "적용 시 바로 잠김"
                    : preview.NextLockStartLocalTime is DateTime next
                        ? $"다음 잠금 · {next.ToString("dddd HH:mm", CultureInfo.GetCultureInfo("ko-KR"))}"
                        : "설정된 잠금 없음";
            }
        }
        catch (ArgumentException exception)
        {
            validation = exception.Message;
            impact = "입력값 확인 필요";
        }

        SetField(ref _weeklyScheduleValidationMessage, validation, nameof(WeeklyScheduleValidationMessage));
        SetField(ref _weeklyScheduleImpact, impact, nameof(WeeklyScheduleImpact));
        SetField(ref _willLockImmediately, immediate, nameof(WillLockImmediately));
        if (!immediate && IsWeeklyScheduleConfirmationVisible)
        {
            ClearWeeklyScheduleConfirmation();
        }

        OnPropertyChanged(nameof(HasWeeklyScheduleChanges));
        OnPropertyChanged(nameof(WeeklyScheduleChangeStatus));
        UpdateWeeklyScheduleCommands();
    }

    private void UpdateWeeklyScheduleCommands()
    {
        OnPropertyChanged(nameof(CanEditWeeklySchedule));
        _saveWeeklyScheduleCommand?.NotifyCanExecuteChanged();
        _revertWeekdayCommand?.NotifyCanExecuteChanged();
        _discardWeeklyScheduleCommand?.NotifyCanExecuteChanged();
        _confirmWeeklyScheduleCommand?.NotifyCanExecuteChanged();
    }
}
