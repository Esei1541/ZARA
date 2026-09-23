using System.Globalization;
using System.Windows.Input;
using Zara.Application.UsagePolicy;
using Zara.Desktop.Commands;

namespace Zara.Desktop.ViewModels;

internal sealed partial class MainWindowViewModel
{
    private AsyncCommand[] _emergencyAdjustmentCommands = [];
    private string _headerLockLabel = "잠금 상태";
    private string _headerLockValue = "불러오는 중";
    private string _headerLockDetail = string.Empty;
    private string _headerEmergencyRemaining = string.Empty;
    private string _headerEmergencyReset = string.Empty;
    private bool _isEmergencyQuotaVisible;

    public string HeaderLockLabel { get => _headerLockLabel; private set => SetField(ref _headerLockLabel, value); }
    public string HeaderLockValue { get => _headerLockValue; private set => SetField(ref _headerLockValue, value); }
    public string HeaderLockDetail { get => _headerLockDetail; private set => SetField(ref _headerLockDetail, value); }
    public string HeaderEmergencyRemaining { get => _headerEmergencyRemaining; private set => SetField(ref _headerEmergencyRemaining, value); }
    public string HeaderEmergencyReset { get => _headerEmergencyReset; private set => SetField(ref _headerEmergencyReset, value); }
    public bool IsEmergencyQuotaVisible { get => _isEmergencyQuotaVisible; private set => SetField(ref _isEmergencyQuotaVisible, value); }

    public ICommand DecreaseEmergencyDurationCommand => _emergencyAdjustmentCommands[0];
    public ICommand IncreaseEmergencyDurationCommand => _emergencyAdjustmentCommands[1];
    public ICommand DecreaseEmergencySentenceCountCommand => _emergencyAdjustmentCommands[2];
    public ICommand IncreaseEmergencySentenceCountCommand => _emergencyAdjustmentCommands[3];
    public ICommand DecreaseEmergencyMaximumCommand => _emergencyAdjustmentCommands[4];
    public ICommand IncreaseEmergencyMaximumCommand => _emergencyAdjustmentCommands[5];

    private void UpdateHeaderPresentation(UsagePolicyRuntimeSnapshot snapshot)
    {
        HeaderLockLabel = snapshot.Evaluation.LockRequired ? "잠금 상태" : "다음 잠금까지";
        if (snapshot.Evaluation.LockRequired)
        {
            HeaderLockValue = "잠금 중";
            HeaderLockDetail = "사용 금지 시간";
        }
        else if (snapshot.NextLockStartLocalTime is DateTime next)
        {
            long seconds = Math.Max(0, (long)Math.Ceiling((next - snapshot.EvaluatedLocalTime).TotalSeconds));
            HeaderLockValue = string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}");
            HeaderLockDetail = next.Date == snapshot.EvaluatedLocalTime.Date
                ? string.Create(CultureInfo.InvariantCulture, $"오늘 {next:HH:mm}")
                : next.ToString("M월 d일 dddd HH:mm", CultureInfo.GetCultureInfo("ko-KR"));
            if (snapshot.Evaluation.HasActiveEmergencyUnlock)
            {
                HeaderLockDetail = $"긴급 해제 중 · {HeaderLockDetail}";
            }
            else if (snapshot.Evaluation.HasActiveReservation)
            {
                HeaderLockDetail = $"예약 사용 중 · {HeaderLockDetail}";
            }
        }
        else
        {
            HeaderLockValue = "잠금 없음";
            HeaderLockDetail = snapshot.Evaluation.HasActiveEmergencyUnlock ? "긴급 해제 중"
                : snapshot.Evaluation.HasActiveReservation ? "예약 사용 중" : string.Empty;
        }

        IsEmergencyQuotaVisible = snapshot.EmergencyUnlockRemainingCount.HasValue;
        HeaderEmergencyRemaining = snapshot.EmergencyUnlockRemainingCount is int remaining
            ? string.Create(CultureInfo.InvariantCulture, $"{remaining}회") : string.Empty;
        HeaderEmergencyReset = IsEmergencyQuotaVisible
            ? $"{CultureInfo.GetCultureInfo("ko-KR").DateTimeFormat.GetDayName(snapshot.Settings.EmergencyUnlock.WeeklyResetDay)} 초기화"
            : string.Empty;
    }

    private void InitializeEmergencyAdjustmentCommands()
    {
        _emergencyAdjustmentCommands =
        [
            CreateEmergencyAdjustment(() => EmergencyDurationMinutesText, value => EmergencyDurationMinutesText = value, -1, 1, 60),
            CreateEmergencyAdjustment(() => EmergencyDurationMinutesText, value => EmergencyDurationMinutesText = value, 1, 1, 60),
            CreateEmergencyAdjustment(() => EmergencySentenceCountText, value => EmergencySentenceCountText = value, -1, 0, 99),
            CreateEmergencyAdjustment(() => EmergencySentenceCountText, value => EmergencySentenceCountText = value, 1, 0, 99),
            CreateEmergencyAdjustment(() => EmergencyWeeklyMaximumCountText, value => EmergencyWeeklyMaximumCountText = value, -1, 1, 99, weekly: true),
            CreateEmergencyAdjustment(() => EmergencyWeeklyMaximumCountText, value => EmergencyWeeklyMaximumCountText = value, 1, 1, 99, weekly: true),
        ];
    }

    private AsyncCommand CreateEmergencyAdjustment(Func<string> read, Action<string> write, int delta, int minimum, int maximum, bool weekly = false) => new(
        () =>
        {
            int current = int.Parse(read(), CultureInfo.InvariantCulture);
            write((current + delta).ToString(CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        },
        ReportEmergencyUnlockSettingsFailure,
        () => CanChangeUsagePolicySettings && (!weekly || EmergencyWeeklyLimitEnabled) &&
            int.TryParse(read(), NumberStyles.None, CultureInfo.InvariantCulture, out int current) &&
            current >= minimum && current <= maximum && current + delta >= minimum && current + delta <= maximum);

    private void NotifyEmergencyAdjustments()
    {
        foreach (AsyncCommand command in _emergencyAdjustmentCommands)
        {
            command.NotifyCanExecuteChanged();
        }
    }
}
