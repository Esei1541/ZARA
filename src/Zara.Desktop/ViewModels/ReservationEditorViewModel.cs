using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Zara.Desktop.ViewModels;

/// <summary>
/// Holds the UI-only fields of the time-outside-use reservation dialog.
/// </summary>
internal sealed class ReservationEditorViewModel : INotifyPropertyChanged
{
    private DateTime? _selectedDate = DateTime.Today;
    private string _memo = string.Empty;

    /// <summary>Initializes the dialog with the current date and midnight-to-one-hour interval.</summary>
    public ReservationEditorViewModel()
    {
        StartTime = new TimeSelectionViewModel();
        EndTime = new TimeSelectionViewModel();
        StartTime.Set(TimeOnly.MinValue);
        EndTime.Set(new TimeOnly(1, 0));
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
    /// Validates the dialog fields and creates a raw result. Same-day interval validation remains
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
