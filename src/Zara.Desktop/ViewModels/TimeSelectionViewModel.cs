using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Zara.Desktop.ViewModels;

/// <summary>
/// Exposes a local time as the AM/PM, hour, and minute controls used by the WPF settings forms.
/// </summary>
internal sealed class TimeSelectionViewModel : INotifyPropertyChanged
{
    private readonly bool _use24HourClock;
    private string _meridiem = "오전";
    private int _hour = 12;
    private int _minute;
    private int _lastValidMinutes;
    private string _hourText = "12";
    private string _minuteText = "0";
    private readonly IReadOnlyList<string> _meridiems = MeridiemValues;
    private readonly IReadOnlyList<int> _hours = HourValues;
    private readonly IReadOnlyList<int> _minutes = MinuteValues;

    private static readonly IReadOnlyList<string> MeridiemValues = ["오전", "오후"];
    private static readonly IReadOnlyList<int> HourValues = Enumerable.Range(1, 12).ToArray();
    private static readonly IReadOnlyList<int> MinuteValues = Enumerable.Range(0, 60).ToArray();

    internal TimeSelectionViewModel(bool use24HourClock = false)
    {
        _use24HourClock = use24HourClock;
        if (use24HourClock)
        {
            _hour = 0;
            _hourText = "00";
            _minuteText = "00";
        }
    }

    public bool IsValid => TryGetTime(out _);

    public string DisplayTime => TryGetTime(out TimeOnly time)
        ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
        : "--:--";

    public double MinutesSinceMidnight
    {
        get => TryGetTime(out TimeOnly time) ? time.Hour * 60 + time.Minute : _lastValidMinutes;
        set
        {
            int minutes = (int)Math.Clamp(Math.Round(value), 0, 1439);
            Set(new TimeOnly(minutes / 60, minutes % 60));
        }
    }

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

            if (TryParseNumber(value, _use24HourClock ? 0 : 1, _use24HourClock ? 23 : 12, out int parsedHour) &&
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
        int hour = ParseNumber(HourText, _use24HourClock ? 0 : 1, _use24HourClock ? 23 : 12, displayName, "시간");
        int minute = ParseNumber(MinuteText, minimum: 0, maximum: 59, displayName, "분");
        if (_use24HourClock)
        {
            return new TimeOnly(hour, minute);
        }

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
        if (_use24HourClock)
        {
            HourText = value.Hour.ToString("00", CultureInfo.InvariantCulture);
            MinuteText = value.Minute.ToString("00", CultureInfo.InvariantCulture);
            return;
        }

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

    private bool TryGetTime(out TimeOnly time)
    {
        time = default;
        if (!TryParseNumber(HourText, _use24HourClock ? 0 : 1, _use24HourClock ? 23 : 12, out int hour) ||
            !TryParseNumber(MinuteText, 0, 59, out int minute) ||
            (!_use24HourClock && Meridiem is not ("오전" or "오후")))
        {
            return false;
        }

        time = new TimeOnly(_use24HourClock ? hour : hour % 12 + (Meridiem == "오후" ? 12 : 0), minute);
        return true;
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (TryGetTime(out TimeOnly time))
        {
            _lastValidMinutes = time.Hour * 60 + time.Minute;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(HourText) or nameof(MinuteText) or nameof(Meridiem))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsValid)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTime)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MinutesSinceMidnight)));
        }
    }
}
