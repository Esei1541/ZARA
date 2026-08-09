using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Zara.Application.Locking;
using Zara.Core.Runtime;
using Zara.Desktop.Commands;

namespace Zara.Desktop.ViewModels;

/// <summary>
/// Projects the overlay demonstration state and delegates all transitions to the Application use case.
/// </summary>
internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ILockRuntimeUseCase _lockRuntime;
    private readonly AsyncCommand _startLockDemoCommand;
    private string _statusText = "오버레이 시연을 시작할 준비가 되었습니다.";
    private bool _isOverlayActive;

    internal MainWindowViewModel(
        ILockRuntimeUseCase lockRuntime,
        Func<Task> requestExit)
    {
        _lockRuntime = lockRuntime ?? throw new ArgumentNullException(nameof(lockRuntime));
        ArgumentNullException.ThrowIfNull(requestExit);

        _startLockDemoCommand = new AsyncCommand(
            StartLockDemoAsync,
            exception => ReportOperationFailure("오버레이를 시작하지 못했습니다.", exception),
            () => !IsOverlayActive);
        ExitCommand = new AsyncCommand(
            requestExit,
            exception => ReportOperationFailure("안전하게 종료하지 못했습니다.", exception));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand StartLockDemoCommand => _startLockDemoCommand;

    public ICommand ExitCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool IsOverlayActive
    {
        get => _isOverlayActive;
        private set
        {
            if (_isOverlayActive == value)
            {
                return;
            }

            _isOverlayActive = value;
            OnPropertyChanged();
            _startLockDemoCommand.NotifyCanExecuteChanged();
        }
    }

    internal void RefreshRuntimeState()
    {
        RuntimeState state = _lockRuntime.CurrentState;
        IsOverlayActive =
            state.DesiredLock == LockState.Locked &&
            state.OverlayProjection == OverlayProjectionState.Visible;

        StatusText = (state.DesiredLock, state.OverlayProjection) switch
        {
            (LockState.Locked, OverlayProjectionState.Visible) =>
                "모든 모니터에 오버레이를 표시했습니다. 각 화면의 개발 즉시 해제로 전체 오버레이를 제거할 수 있습니다.",
            (LockState.Unlocked, OverlayProjectionState.Hidden) =>
                "오버레이가 해제되었으며 다시 시연할 수 있습니다.",
            (_, OverlayProjectionState.Unknown) =>
                "오버레이 투영 결과를 확인할 수 없습니다. 종료하지 말고 개발 즉시 해제를 다시 시도하십시오.",
            _ => "오버레이 상태를 조정하고 있습니다.",
        };
    }

    internal void ReportOperationFailure(string context, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentNullException.ThrowIfNull(exception);

        RefreshRuntimeState();
        StatusText = $"{context} {exception.Message}";
    }

    private async Task StartLockDemoAsync()
    {
        await _lockRuntime.RequestLockAsync().ConfigureAwait(true);
        RefreshRuntimeState();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
