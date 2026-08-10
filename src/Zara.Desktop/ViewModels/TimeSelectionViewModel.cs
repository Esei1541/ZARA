using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Zara.Desktop.ViewModels;

/// <summary>
/// Exposes a local time as the AM/PM, hour, and minute controls used by the WPF settings forms.
/// </summary>
internal sealed class TimeSelectionViewModel : INotifyPropertyChanged
{
    private string _meridiem = "오전";
    private int _hour = 12;
    private int _minute;
    private readonly IReadOnlyList<string> _meridiems = MeridiemValues;
    private readonly IReadOnlyList<int> _hours = HourValues;
    private readonly IReadOnlyList<int> _minutes = MinuteValues;

    private static readonly IReadOnlyList<string> MeridiemValues = ["오전", "오후"];
    private static readonly IReadOnlyList<int> HourValues = Enumerable.Range(1, 12).ToArray();
    private static readonly IReadOnlyList<int> MinuteValues = Enumerable.Range(0, 60).ToArray();

    /// <summary>Gets the selectable Korean AM/PM labels.</summary>
    public IReadOnlyList<string> Meridiems => _meridiems;

    /// <summary>Gets the selectable twelve-hour clock values.</summary>
    public IReadOnlyList<int> Hours => _hours;

    /// <summary>Gets the selectable minute values.</summary>
    public IReadOnlyList<int> Minutes => _minutes;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets or sets the selected AM/PM label.</summary>
    public string Meridiem
    {
        get => _meridiem;
        set
        {
            if (string.Equals(_meridiem, value, StringComparison.Ordinal))
            {
                return;
            }

            _meridiem = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Gets or sets the selected hour in the twelve-hour clock.</summary>
    public int Hour
    {
        get => _hour;
        set
        {
            if (_hour == value)
            {
                return;
            }

            _hour = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Gets or sets the selected minute.</summary>
    public int Minute
    {
        get => _minute;
        set
        {
            if (_minute == value)
            {
                return;
            }

            _minute = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Returns the selected controls as a <see cref="TimeOnly"/> value.</summary>
    public TimeOnly ToTimeOnly()
    {
        int hour24 = Meridiem switch
        {
            "오전" when Hour == 12 => 0,
            "오후" when Hour != 12 => Hour + 12,
            "오후" => 12,
            _ => Hour,
        };
        return new TimeOnly(hour24, Minute);
    }

    /// <summary>Projects an existing local time into the controls.</summary>
    public void Set(TimeOnly value)
    {
        Meridiem = value.Hour < 12 ? "오전" : "오후";
        Hour = value.Hour % 12 == 0 ? 12 : value.Hour % 12;
        Minute = value.Minute;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
