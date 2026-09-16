using System.ComponentModel;
using System.Runtime.CompilerServices;
using Zara.Core.UsagePolicy;

namespace Zara.Desktop.ViewModels;

/// <summary>
/// Projects one immutable reservation into a sortable list row and exposes display-only state.
/// </summary>
internal sealed class ReservationRowViewModel : INotifyPropertyChanged
{
    private bool _isExpired;
    private bool _isActive;

    /// <summary>Creates a row for the supplied persisted reservation.</summary>
    public ReservationRowViewModel(OutOfHoursReservation reservation)
    {
        Reservation = reservation ?? throw new ArgumentNullException(nameof(reservation));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the immutable persisted reservation represented by the row.</summary>
    public OutOfHoursReservation Reservation { get; }

    public Guid Id => Reservation.Id;

    public DateOnly Date => Reservation.Date;

    public TimeOnly StartTime => Reservation.StartTime;

    public TimeOnly EndTime => Reservation.EndTime;

    public string Memo => Reservation.Memo;

    /// <summary>Gets whether the reservation belongs to a past date.</summary>
    public bool IsExpired
    {
        get => _isExpired;
        private set => SetField(ref _isExpired, value);
    }

    /// <summary>Gets whether the current local time is inside this reservation.</summary>
    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetField(ref _isActive, value))
            {
                OnPropertyChanged(nameof(ActiveStatusText));
            }
        }
    }

    /// <summary>Gets the Korean status label displayed in the reservation list.</summary>
    public string ActiveStatusText => IsActive ? "적용 중" : "대기";

    /// <summary>Refreshes UI-only appearance from the supplied local time.</summary>
    public void UpdatePresentation(DateTime localNow)
    {
        DateOnly localDate = DateOnly.FromDateTime(localNow);
        TimeOnly localTime = TimeOnly.FromDateTime(localNow);
        IsExpired = Date < localDate;
        IsActive = Date == localDate && StartTime <= localTime && localTime < EndTime;
    }

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
