using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Zara.Core.UsagePolicy;
using Zara.Desktop.Commands;

namespace Zara.Desktop.ViewModels;

/// <summary>Holds a temporary time interval and the weekdays to receive it before saving.</summary>
internal sealed class BulkUsageRestrictionViewModel : INotifyPropertyChanged
{
    private readonly AsyncCommand[] _adjustmentCommands;
    private bool _activateAfterApply = true;
    private bool _isReleaseSelected;
    private bool _loading;

    internal BulkUsageRestrictionViewModel(IEnumerable<KeyValuePair<DayOfWeek, string>> weekdays)
    {
        Targets = weekdays.Select(day => new BulkWeekdaySelectionViewModel(day.Key, day.Value)).ToArray();
        foreach (BulkWeekdaySelectionViewModel target in Targets)
        {
            target.PropertyChanged += OnTargetChanged;
        }

        StartTime = new TimeSelectionViewModel(use24HourClock: true);
        ReleaseTime = new TimeSelectionViewModel(use24HourClock: true);
        StartTime.PropertyChanged += OnTimeChanged;
        ReleaseTime.PropertyChanged += OnTimeChanged;
        _adjustmentCommands = new[] { -60, -30, 30, 60 }.Select(CreateAdjustmentCommand).ToArray();
        SelectAllCommand = CreateSelectionCommand(isSelected: true);
        ClearSelectionCommand = CreateSelectionCommand(isSelected: false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event EventHandler? Edited;

    public IReadOnlyList<BulkWeekdaySelectionViewModel> Targets { get; }
    public TimeSelectionViewModel StartTime { get; }
    public TimeSelectionViewModel ReleaseTime { get; }
    public TimeSelectionViewModel SelectedTime => IsReleaseSelected ? ReleaseTime : StartTime;
    public string DisplayName { get; } = "선택한 요일";
    public string SelectedTimeLabel => IsReleaseSelected ? "잠금 해제 시각" : "잠금 시작 시각";
    public int SelectedDayCount => Targets.Count(day => day.IsSelected);
    public string SelectionSummary => $"{SelectedDayCount}개 선택";
    public string ApplyButtonText => $"{SelectedDayCount}개 요일에 시간 반영";
    public string EndDay => StartTime.IsValid && ReleaseTime.IsValid &&
        ReleaseTime.ToTimeOnly() < StartTime.ToTimeOnly() ? "다음 날" : "같은 날";

    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand EarlierHourCommand => _adjustmentCommands[0];
    public ICommand EarlierHalfHourCommand => _adjustmentCommands[1];
    public ICommand LaterHalfHourCommand => _adjustmentCommands[2];
    public ICommand LaterHourCommand => _adjustmentCommands[3];

    public bool ActivateAfterApply
    {
        get => _activateAfterApply;
        set
        {
            if (_activateAfterApply != value)
            {
                _activateAfterApply = value;
                OnPropertyChanged();
                NotifyEdit();
            }
        }
    }

    public bool IsStartSelected
    {
        get => !_isReleaseSelected;
        set => IsReleaseSelected = !value;
    }

    public bool IsReleaseSelected
    {
        get => _isReleaseSelected;
        set
        {
            if (_isReleaseSelected == value)
            {
                return;
            }

            _isReleaseSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStartSelected));
            OnPropertyChanged(nameof(SelectedTime));
            OnPropertyChanged(nameof(SelectedTimeLabel));
            NotifyAdjustmentCommands();
        }
    }

    public string ValidationMessage
    {
        get
        {
            try
            {
                _ = ToRestriction();
                return string.Empty;
            }
            catch (ArgumentException exception)
            {
                return exception.Message;
            }
        }
    }

    public string DraftSummary
    {
        get
        {
            if (!string.IsNullOrEmpty(ValidationMessage))
            {
                return "입력값 확인 필요";
            }

            DailyUsageRestriction rule = ToRestriction();
            int duration = (rule.ReleaseTime.Hour * 60 + rule.ReleaseTime.Minute -
                rule.StartTime.Hour * 60 - rule.StartTime.Minute + 1440) % 1440;
            string length = duration >= 60 ? $"{duration / 60}시간" : string.Empty;
            if (duration % 60 != 0)
            {
                length += $" {duration % 60}분";
            }

            string nextDay = rule.ReleaseTime < rule.StartTime ? "다음 날 " : string.Empty;
            return string.Create(CultureInfo.InvariantCulture,
                $"{rule.StartTime:HH:mm} → {nextDay}{rule.ReleaseTime:HH:mm} · {length.Trim()}");
        }
    }

    internal DailyUsageRestriction ToRestriction()
    {
        TimeOnly start = StartTime.ToTimeOnly("한 번에 적용 시작 시각");
        TimeOnly release = ReleaseTime.ToTimeOnly("한 번에 적용 해제 시각");
        if (start == release)
        {
            throw new ArgumentException("시작 시각과 해제 시각은 다르게 입력하세요.");
        }

        return new DailyUsageRestriction(isEnabled: true, start, release);
    }

    internal void Reset(DailyUsageRestriction restriction)
    {
        _loading = true;
        try
        {
            foreach (BulkWeekdaySelectionViewModel target in Targets)
            {
                target.IsSelected = false;
            }

            ActivateAfterApply = true;
            StartTime.Set(restriction.StartTime);
            ReleaseTime.Set(restriction.ReleaseTime);
            IsReleaseSelected = false;
        }
        finally
        {
            _loading = false;
        }

        NotifyEdit();
    }

    private AsyncCommand CreateSelectionCommand(bool isSelected) => new(
        () =>
        {
            foreach (BulkWeekdaySelectionViewModel target in Targets)
            {
                target.IsSelected = isSelected;
            }

            return Task.CompletedTask;
        },
        exception => System.Diagnostics.Trace.TraceError("Weekday selection failed: {0}", exception));

    private AsyncCommand CreateAdjustmentCommand(int delta) => new(
        () =>
        {
            SelectedTime.MinutesSinceMidnight += delta;
            return Task.CompletedTask;
        },
        exception => System.Diagnostics.Trace.TraceError("Time adjustment failed: {0}", exception),
        () => SelectedTime.IsValid && SelectedTime.MinutesSinceMidnight + delta is >= 0 and <= 1439);

    private void OnTargetChanged(object? sender, PropertyChangedEventArgs e) => NotifyEdit();

    private void OnTimeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimeSelectionViewModel.DisplayTime))
        {
            NotifyEdit();
        }
    }

    private void NotifyEdit()
    {
        if (_loading)
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedDayCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(ApplyButtonText));
        OnPropertyChanged(nameof(EndDay));
        OnPropertyChanged(nameof(DraftSummary));
        OnPropertyChanged(nameof(ValidationMessage));
        NotifyAdjustmentCommands();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyAdjustmentCommands()
    {
        foreach (AsyncCommand command in _adjustmentCommands)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
