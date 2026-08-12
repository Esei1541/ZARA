using System.ComponentModel;
using System.Globalization;
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
    private string _hourText = "12";
    private string _minuteText = "0";
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
            if (_hour != value)
            {
                _hour = value;
                OnPropertyChanged();
            }

            HourText = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Gets or sets the selected minute.</summary>
    public int Minute
    {
        get => _minute;
        set
        {
            if (_minute != value)
            {
                _minute = value;
                OnPropertyChanged();
            }

            MinuteText = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Gets or sets the hour text used by direct-entry controls. Invalid partial input remains
    /// visible until the containing form is saved.
    /// </summary>
    public string HourText
    {
        get => _hourText;
        set
        {
            if (!SetField(ref _hourText, value))
            {
                return;
            }

            if (TryParseNumber(value, minimum: 1, maximum: 12, out int parsedHour) &&
                _hour != parsedHour)
            {
                _hour = parsedHour;
                OnPropertyChanged(nameof(Hour));
            }
        }
    }

    /// <summary>
    /// Gets or sets the minute text used by direct-entry controls. Invalid partial input remains
    /// visible until the containing form is saved.
    /// </summary>
    public string MinuteText
    {
        get => _minuteText;
        set
        {
            if (!SetField(ref _minuteText, value))
            {
                return;
            }

            if (TryParseNumber(value, minimum: 0, maximum: 59, out int parsedMinute) &&
                _minute != parsedMinute)
            {
                _minute = parsedMinute;
                OnPropertyChanged(nameof(Minute));
            }
        }
    }

    /// <summary>
    /// Returns the selected controls as a <see cref="TimeOnly"/> value after validating direct
    /// hour and minute input.
    /// </summary>
    /// <param name="displayName">The field name included in a user-visible validation error.</param>
    public TimeOnly ToTimeOnly(string displayName = "시각")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        int hour = ParseNumber(HourText, minimum: 1, maximum: 12, displayName, "시간");
        int minute = ParseNumber(MinuteText, minimum: 0, maximum: 59, displayName, "분");
        int hour24 = Meridiem switch
        {
            "오전" when hour == 12 => 0,
            "오전" => hour,
            "오후" when hour != 12 => hour + 12,
            "오후" => 12,
            _ => throw new ArgumentException($"{displayName}의 오전/오후를 선택하세요."),
        };
        return new TimeOnly(hour24, minute);
    }

    /// <summary>Projects an existing local time into the controls.</summary>
    public void Set(TimeOnly value)
    {
        Meridiem = value.Hour < 12 ? "오전" : "오후";
        Hour = value.Hour % 12 == 0 ? 12 : value.Hour % 12;
        Minute = value.Minute;
    }

    private static int ParseNumber(
        string value,
        int minimum,
        int maximum,
        string displayName,
        string componentName)
    {
        if (!TryParseNumber(value, minimum, maximum, out int parsedValue))
        {
            throw new ArgumentException(
                $"{displayName}의 {componentName}은 {minimum}부터 {maximum}까지의 정수로 입력하세요.");
        }

        return parsedValue;
    }

    private static bool TryParseNumber(
        string value,
        int minimum,
        int maximum,
        out int parsedValue) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out parsedValue) &&
        parsedValue >= minimum &&
        parsedValue <= maximum;

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
