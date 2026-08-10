using System.ComponentModel;
using System.Runtime.CompilerServices;
using Zara.Core.UsagePolicy;

namespace Zara.Desktop.ViewModels;

/// <summary>
/// Edits one weekday's usage restriction while leaving all policy validation to Core.
/// </summary>
internal sealed class DailyUsageRestrictionViewModel : INotifyPropertyChanged
{
    private bool _isEnabled;

    /// <summary>Creates a weekday editor with the supplied display name.</summary>
    public DailyUsageRestrictionViewModel(DayOfWeek dayOfWeek, string displayName)
    {
        DayOfWeek = dayOfWeek;
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        StartTime = new TimeSelectionViewModel();
        ReleaseTime = new TimeSelectionViewModel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the weekday represented by this row.</summary>
    public DayOfWeek DayOfWeek { get; }

    /// <summary>Gets the Korean label displayed for the weekday.</summary>
    public string DisplayName { get; }

    /// <summary>Gets or sets whether the weekday rule is enabled.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Gets the restriction start-time controls.</summary>
    public TimeSelectionViewModel StartTime { get; }

    /// <summary>Gets the restriction release-time controls.</summary>
    public TimeSelectionViewModel ReleaseTime { get; }

    /// <summary>Creates the immutable Core rule represented by this editor.</summary>
    public DailyUsageRestriction ToRestriction() => new(
        IsEnabled,
        StartTime.ToTimeOnly(),
        ReleaseTime.ToTimeOnly());

    /// <summary>Updates this editor from a persisted Core rule.</summary>
    public void Load(DailyUsageRestriction restriction)
    {
        ArgumentNullException.ThrowIfNull(restriction);
        IsEnabled = restriction.IsEnabled;
        StartTime.Set(restriction.StartTime);
        ReleaseTime.Set(restriction.ReleaseTime);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
