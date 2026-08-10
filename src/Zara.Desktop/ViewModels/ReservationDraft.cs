namespace Zara.Desktop.ViewModels;

/// <summary>
/// Holds reservation form values before the main view model constructs the Core reservation.
/// </summary>
internal sealed record ReservationDraft(
    DateOnly Date,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Memo);
