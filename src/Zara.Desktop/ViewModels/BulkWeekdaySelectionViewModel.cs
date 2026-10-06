using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Zara.Desktop.ViewModels;

internal sealed class BulkWeekdaySelectionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    internal BulkWeekdaySelectionViewModel(DayOfWeek dayOfWeek, string displayName)
    {
        DayOfWeek = dayOfWeek;
        DisplayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DayOfWeek DayOfWeek { get; }
    public string DisplayName { get; }
    public string ShortName => DisplayName[..1];

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
