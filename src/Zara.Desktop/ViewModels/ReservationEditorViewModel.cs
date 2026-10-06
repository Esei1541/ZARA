using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Zara.Desktop.ViewModels;

/// <summary>
/// Holds the UI-only fields of the time-outside-use reservation dialog.
/// </summary>
internal sealed class ReservationEditorViewModel : INotifyPropertyChanged
{
    private DateTime? _selectedDate;
    private string _memo = string.Empty;

    /// <summary>Initializes form values once, leaving subsequent edits under the user's control.</summary>
    public ReservationEditorViewModel(ReservationDraft? initialDraft = null)
    {
        _selectedDate = initialDraft?.Date.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        _memo = initialDraft?.Memo ?? string.Empty;
        StartTime = new TimeSelectionViewModel(use24HourClock: true);
        EndTime = new TimeSelectionViewModel(use24HourClock: true);
        StartTime.Set(initialDraft?.StartTime ?? TimeOnly.MinValue);
        EndTime.Set(initialDraft?.EndTime ?? new TimeOnly(1, 0));
        StartTime.PropertyChanged += OnTimeChanged;
        EndTime.PropertyChanged += OnTimeChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets or sets the selected reservation date.</summary>
    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (_selectedDate == value)
            {
                return;
            }

            _selectedDate = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Gets the start-time controls.</summary>
    public TimeSelectionViewModel StartTime { get; }

    /// <summary>Gets the end-time controls.</summary>
    public TimeSelectionViewModel EndTime { get; }

    /// <summary>Names the end field and indicates a following-day end for valid input.</summary>
    public string EndTimeLabel => StartTime.IsValid && EndTime.IsValid &&
        EndTime.ToTimeOnly() < StartTime.ToTimeOnly()
            ? "종료 시각 (다음 날)"
            : "종료 시각";

    /// <summary>Gets or sets the user-visible memo.</summary>
    public string Memo
    {
        get => _memo;
        set
        {
            if (string.Equals(_memo, value, StringComparison.Ordinal))
            {
                return;
            }

            _memo = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Validates the dialog fields and creates a raw result. Interval validation remains
    /// the responsibility of the Core reservation constructor.
    /// </summary>
    /// <param name="draft">The completed raw result when every dialog field is valid.</param>
    /// <param name="validationMessage">A Korean message identifying the first invalid field.</param>
    /// <returns><see langword="true"/> when the dialog fields can be converted to a result.</returns>
    public bool TryCreateDraft(
        out ReservationDraft? draft,
        out string validationMessage)
    {
        draft = null;
        if (SelectedDate is not DateTime selectedDate)
        {
            validationMessage = "날짜를 선택해주세요.";
            return false;
        }

        try
        {
            draft = new ReservationDraft(
                DateOnly.FromDateTime(selectedDate),
                StartTime.ToTimeOnly("시작 시각"),
                EndTime.ToTimeOnly("종료 시각"),
                Memo);
            validationMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            validationMessage = exception.Message;
            return false;
        }
    }

    private void OnTimeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimeSelectionViewModel.DisplayTime))
        {
            OnPropertyChanged(nameof(EndTimeLabel));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
