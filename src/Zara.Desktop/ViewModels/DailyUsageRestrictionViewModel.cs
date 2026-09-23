using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Zara.Core.UsagePolicy;
using Zara.Desktop.Commands;

namespace Zara.Desktop.ViewModels;

/// <summary>
/// Edits one weekday's usage restriction while leaving all policy validation to Core.
/// </summary>
internal sealed class DailyUsageRestrictionViewModel : INotifyPropertyChanged
{
    private bool _isRestrictionEnabled;
    private bool _isReleaseSelected;
    private bool _loading;
    private DailyUsageRestriction _savedRestriction = DailyUsageRestriction.Disabled;
    private readonly AsyncCommand[] _adjustmentCommands;

    /// <summary>Creates a weekday editor with the supplied display name.</summary>
    public DailyUsageRestrictionViewModel(DayOfWeek dayOfWeek, string displayName)
    {
        DayOfWeek = dayOfWeek;
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        StartTime = new TimeSelectionViewModel(use24HourClock: true);
        ReleaseTime = new TimeSelectionViewModel(use24HourClock: true);
        _adjustmentCommands = new[] { -60, -30, 30, 60 }.Select(CreateAdjustmentCommand).ToArray();
        StartTime.PropertyChanged += OnTimeChanged;
        ReleaseTime.PropertyChanged += OnTimeChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal event EventHandler? Edited;

    public ICommand EarlierHourCommand => _adjustmentCommands[0];
    public ICommand EarlierHalfHourCommand => _adjustmentCommands[1];
    public ICommand LaterHalfHourCommand => _adjustmentCommands[2];
    public ICommand LaterHourCommand => _adjustmentCommands[3];

    public string EditorTitle => $"{DisplayName} 편집";
    public string SavedSummary => FormatRestriction(_savedRestriction);
    public string SavedDescription => $"현재 적용: {SavedSummary}";
    public string ChangeStatus => HasChanges ? "편집 중 · 저장 전" : "적용 중";
    public string EndDay => StartTime.IsValid && ReleaseTime.IsValid &&
        ReleaseTime.ToTimeOnly() < StartTime.ToTimeOnly() ? "다음 날 해제" : "같은 날";

    public bool HasChanges => !TryGetRestriction(out DailyUsageRestriction? rule) || rule != _savedRestriction;

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
            if (!TryGetRestriction(out DailyUsageRestriction? rule))
            {
                return "입력값 확인 필요";
            }

            if (!rule!.IsEnabled)
            {
                return $"{DisplayName} 잠금 없음";
            }

            int duration = (rule.ReleaseTime.Hour * 60 + rule.ReleaseTime.Minute -
                rule.StartTime.Hour * 60 - rule.StartTime.Minute + 1440) % 1440;
            string length = duration >= 60 ? $"{duration / 60}시간" : string.Empty;
            if (duration % 60 != 0)
            {
                length += $" {duration % 60}분";
            }

            return $"{DisplayName} {FormatRestriction(rule)} · {length.Trim()}";
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

    public TimeSelectionViewModel SelectedTime => IsReleaseSelected ? ReleaseTime : StartTime;
    public string SelectedTimeLabel => IsReleaseSelected ? "잠금 해제 시각" : "잠금 시작 시각";

    /// <summary>Gets the weekday represented by this row.</summary>
    public DayOfWeek DayOfWeek { get; }

    /// <summary>Gets the Korean label displayed for the weekday.</summary>
    public string DisplayName { get; }

    /// <summary>Gets or sets whether the weekday rule is enabled.</summary>
    public bool IsRestrictionEnabled
    {
        get => _isRestrictionEnabled;
        set
        {
            if (_isRestrictionEnabled == value)
            {
                return;
            }

            _isRestrictionEnabled = value;
            OnPropertyChanged();
            NotifyEdit();
        }
    }

    /// <summary>Gets the restriction start-time controls.</summary>
    public TimeSelectionViewModel StartTime { get; }

    /// <summary>Gets the restriction release-time controls.</summary>
    public TimeSelectionViewModel ReleaseTime { get; }

    /// <summary>
    /// Creates the immutable Core rule represented by this editor and reports the exact invalid
    /// weekday field in Korean.
    /// </summary>
    public DailyUsageRestriction ToRestriction()
    {
        TimeOnly startTime = StartTime.ToTimeOnly($"{DisplayName} 시작 시각");
        TimeOnly releaseTime = ReleaseTime.ToTimeOnly($"{DisplayName} 해제 시각");
        if (IsRestrictionEnabled && startTime == releaseTime)
        {
            throw new ArgumentException(
                $"{DisplayName}의 시작 시각과 해제 시각은 다르게 입력하세요.");
        }

        return new DailyUsageRestriction(IsRestrictionEnabled, startTime, releaseTime);
    }

    /// <summary>Updates this editor from a persisted Core rule.</summary>
    public void Load(DailyUsageRestriction restriction)
    {
        ArgumentNullException.ThrowIfNull(restriction);
        _loading = true;
        try
        {
            _savedRestriction = restriction;
            IsRestrictionEnabled = restriction.IsEnabled;
            StartTime.Set(restriction.StartTime);
            ReleaseTime.Set(restriction.ReleaseTime);
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(SavedSummary));
        OnPropertyChanged(nameof(SavedDescription));
        NotifyEdit();
    }

    internal void Revert() => Load(_savedRestriction);

    private bool TryGetRestriction(out DailyUsageRestriction? rule)
    {
        try
        {
            rule = ToRestriction();
            return true;
        }
        catch (ArgumentException)
        {
            rule = null;
            return false;
        }
    }

    private static string FormatRestriction(DailyUsageRestriction rule) => !rule.IsEnabled
        ? "사용 안 함"
        : string.Create(CultureInfo.InvariantCulture,
            $"{rule.StartTime:HH:mm} → {(rule.ReleaseTime < rule.StartTime ? "다음 날 " : string.Empty)}{rule.ReleaseTime:HH:mm}");

    private AsyncCommand CreateAdjustmentCommand(int delta) => new(
        () =>
        {
            SelectedTime.MinutesSinceMidnight += delta;
            return Task.CompletedTask;
        },
        exception => System.Diagnostics.Trace.TraceError("Time adjustment failed: {0}", exception),
        () => IsRestrictionEnabled && SelectedTime.IsValid &&
            SelectedTime.MinutesSinceMidnight + delta is >= 0 and <= 1439);

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

        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ChangeStatus));
        OnPropertyChanged(nameof(DraftSummary));
        OnPropertyChanged(nameof(EndDay));
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
