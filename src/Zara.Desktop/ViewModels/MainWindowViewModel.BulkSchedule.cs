using System.Windows.Input;
using Zara.Core.UsagePolicy;
using Zara.Desktop.Commands;

namespace Zara.Desktop.ViewModels;

internal sealed partial class MainWindowViewModel
{
    private readonly AsyncCommand _selectBulkScheduleCommand;
    private readonly AsyncCommand _applyBulkScheduleCommand;
    private bool _isBulkScheduleSelected;

    public BulkUsageRestrictionViewModel BulkScheduleEditor { get; }
    public ICommand SelectBulkScheduleCommand => _selectBulkScheduleCommand;
    public ICommand ApplyBulkScheduleCommand => _applyBulkScheduleCommand;
    public bool IsWeekdayScheduleSelected => !IsBulkScheduleSelected;
    public object ActiveScheduleTimeEditor => IsBulkScheduleSelected ? BulkScheduleEditor : SelectedWeekday;
    public bool CanEditScheduleTime => IsBulkScheduleSelected || SelectedWeekday.IsRestrictionEnabled;
    public string ScheduleEditorTitle => IsBulkScheduleSelected ? "한 번에 적용" : SelectedWeekday.EditorTitle;

    public bool IsBulkScheduleSelected
    {
        get => _isBulkScheduleSelected;
        private set
        {
            if (SetField(ref _isBulkScheduleSelected, value))
            {
                NotifyScheduleEditorSelection();
            }
        }
    }

    public DailyUsageRestrictionViewModel? SelectedWeekdayListItem
    {
        get => IsBulkScheduleSelected ? null : SelectedWeekday;
        set
        {
            if (value is not null)
            {
                SelectedWeekday = value;
            }
        }
    }

    private void NotifyScheduleEditorSelection()
    {
        OnPropertyChanged(nameof(IsWeekdayScheduleSelected));
        OnPropertyChanged(nameof(SelectedWeekdayListItem));
        OnPropertyChanged(nameof(ActiveScheduleTimeEditor));
        OnPropertyChanged(nameof(CanEditScheduleTime));
        OnPropertyChanged(nameof(ScheduleEditorTitle));
        _revertWeekdayCommand?.NotifyCanExecuteChanged();
    }

    private bool CanApplyBulkSchedule() =>
        CanEditWeeklySchedule &&
        BulkScheduleEditor.SelectedDayCount > 0 &&
        string.IsNullOrEmpty(BulkScheduleEditor.ValidationMessage);

    private Task ApplyBulkScheduleAsync()
    {
        if (!CanApplyBulkSchedule())
        {
            return Task.CompletedTask;
        }

        DailyUsageRestriction template = BulkScheduleEditor.ToRestriction();
        DayOfWeek[] selectedDays = BulkScheduleEditor.Targets
            .Where(day => day.IsSelected)
            .Select(day => day.DayOfWeek)
            .ToArray();
        _loadingWeeklySchedule = true;
        try
        {
            foreach (DailyUsageRestrictionViewModel editor in WeekdayRestrictions)
            {
                if (selectedDays.Contains(editor.DayOfWeek))
                {
                    editor.ApplyDraft(new DailyUsageRestriction(
                        BulkScheduleEditor.ActivateAfterApply || editor.IsRestrictionEnabled,
                        template.StartTime,
                        template.ReleaseTime));
                }
            }
        }
        finally
        {
            _loadingWeeklySchedule = false;
        }

        ClearWeeklyScheduleConfirmation();
        UpdateWeeklySchedulePresentation();
        RequestNotification(
            "한 번에 적용",
            "선택한 요일에 반영되었습니다.\n일정 저장을 누르면 잠금이 저장됩니다.",
            isError: false);
        return Task.CompletedTask;
    }

    private void OnBulkScheduleEdited(object? sender, EventArgs e)
    {
        if (!_loadingWeeklySchedule)
        {
            _applyBulkScheduleCommand?.NotifyCanExecuteChanged();
        }
    }
}
