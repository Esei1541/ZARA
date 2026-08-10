using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Zara.Desktop.Commands;

namespace Zara.Desktop.ViewModels;

/// <summary>
/// Exposes the desktop shell actions and the last acknowledged restart setting.
/// </summary>
internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly Func<bool, Task> _updateRestartSetting;
    private readonly AsyncCommand _toggleRestartSettingCommand;
    private bool _restartOnExitWhenUnlocked;

    internal MainWindowViewModel(
        bool restartOnExitWhenUnlocked,
        Func<bool, Task> updateRestartSetting,
        Func<Task> requestLock)
    {
        _restartOnExitWhenUnlocked = restartOnExitWhenUnlocked;
        _updateRestartSetting =
            updateRestartSetting ?? throw new ArgumentNullException(nameof(updateRestartSetting));
        ArgumentNullException.ThrowIfNull(requestLock);

        _toggleRestartSettingCommand = new AsyncCommand(
            ToggleRestartSettingAsync,
            static _ => { });
        StartLockDemoCommand = new AsyncCommand(
            requestLock,
            static _ => { });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand ToggleRestartSettingCommand => _toggleRestartSettingCommand;

    public ICommand StartLockDemoCommand { get; }

    public bool RestartOnExitWhenUnlocked
    {
        get => _restartOnExitWhenUnlocked;
        private set
        {
            if (_restartOnExitWhenUnlocked == value)
            {
                return;
            }

            _restartOnExitWhenUnlocked = value;
            OnPropertyChanged();
        }
    }

    private async Task ToggleRestartSettingAsync()
    {
        bool requestedValue = !RestartOnExitWhenUnlocked;

        // A CheckBox toggles before its command runs. Re-project the acknowledged value
        // while the durable setting and the supervision lease are being updated.
        OnPropertyChanged(nameof(RestartOnExitWhenUnlocked));

        try
        {
            await _updateRestartSetting(requestedValue).ConfigureAwait(true);
            RestartOnExitWhenUnlocked = requestedValue;
        }
        catch
        {
            OnPropertyChanged(nameof(RestartOnExitWhenUnlocked));
            throw;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
