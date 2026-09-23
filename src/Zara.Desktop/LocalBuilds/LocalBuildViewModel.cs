#if LOCAL_BUILD_UPDATES
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Zara.Application.LocalBuilds;
using Zara.Desktop.Commands;

namespace Zara.Desktop.LocalBuilds;

/// <summary>Owns the explicitly refreshed local-build screen state.</summary>
internal sealed class LocalBuildViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILocalBuildUpdates _updates;
    private readonly Func<string, string?> _selectDirectory;
    private readonly Func<LocalBuildItemViewModel, bool> _confirmInstall;
    private readonly CancellationTokenSource _lifetimeCancellationSource = new();
    private readonly AsyncCommand _refreshCommand;
    private readonly AsyncCommand _changeDirectoryCommand;
    private readonly AsyncCommand _installSelectedBuildCommand;
    private IReadOnlyList<LocalBuildItemViewModel> _builds = [];
    private LocalBuildItemViewModel? _selectedBuild;
    private string _buildsDirectory = string.Empty;
    private string _currentBuildVersionConfiguration = "현재 빌드 정보를 확인할 수 없습니다.";
    private string _currentBuildBranch = string.Empty;
    private string _currentBuildShortCommit = string.Empty;
    private string? _statusMessage;
    private string? _errorMessage;
    private int _operationInProgress;
    private int _disposeState;

    internal LocalBuildViewModel(
        ILocalBuildUpdates updates,
        Func<string, string?> selectDirectory,
        Func<LocalBuildItemViewModel, bool> confirmInstall)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _selectDirectory = selectDirectory ?? throw new ArgumentNullException(nameof(selectDirectory));
        _confirmInstall = confirmInstall ?? throw new ArgumentNullException(nameof(confirmInstall));
        _refreshCommand = new AsyncCommand(RefreshAsync, ReportUnexpectedFailure, CanStartOperation);
        _changeDirectoryCommand = new AsyncCommand(
            ChangeDirectoryAsync,
            ReportUnexpectedFailure,
            CanStartOperation);
        _installSelectedBuildCommand = new AsyncCommand(
            InstallSelectedBuildAsync,
            ReportUnexpectedFailure,
            CanInstallSelectedBuild);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<LocalBuildItemViewModel> Builds
    {
        get => _builds;
        private set
        {
            _builds = value;
            OnPropertyChanged();
        }
    }

    public LocalBuildItemViewModel? SelectedBuild
    {
        get => _selectedBuild;
        set
        {
            if (!CanInteract || ReferenceEquals(_selectedBuild, value))
            {
                return;
            }

            _selectedBuild = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanInstallSelected));
            OnPropertyChanged(nameof(HasSelectedBuild));
            _installSelectedBuildCommand.NotifyCanExecuteChanged();
        }
    }

    public string BuildsDirectory
    {
        get => _buildsDirectory;
        private set
        {
            _buildsDirectory = value;
            OnPropertyChanged();
        }
    }

    public string CurrentBuildVersionConfiguration
    {
        get => _currentBuildVersionConfiguration;
        private set { _currentBuildVersionConfiguration = value; OnPropertyChanged(); }
    }

    public string CurrentBuildBranch
    {
        get => _currentBuildBranch;
        private set { _currentBuildBranch = value; OnPropertyChanged(); }
    }

    public string CurrentBuildShortCommit
    {
        get => _currentBuildShortCommit;
        private set { _currentBuildShortCommit = value; OnPropertyChanged(); }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy => Volatile.Read(ref _operationInProgress) != 0;
    public bool CanInteract => !IsDisposed && !IsBusy;
    public bool CanInstallSelected => CanInstallSelectedBuild();
    public bool HasSelectedBuild => SelectedBuild is not null;
    public ICommand RefreshCommand => _refreshCommand;
    public ICommand ChangeDirectoryCommand => _changeDirectoryCommand;
    public ICommand InstallSelectedBuildCommand => _installSelectedBuildCommand;

    private bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    internal Task EnterAsync() => LoadAsync();
    internal Task RefreshAsync() => LoadAsync();

    internal async Task ChangeDirectoryAsync()
    {
        if (!TryBeginOperation())
        {
            return;
        }

        try
        {
            string? selectedDirectory = _selectDirectory(BuildsDirectory);
            if (string.IsNullOrWhiteSpace(selectedDirectory))
            {
                return;
            }

            ErrorMessage = null;
            CancellationToken cancellationToken = _lifetimeCancellationSource.Token;
            await _updates
                .ChangeDirectoryAsync(selectedDirectory, cancellationToken)
                .ConfigureAwait(true);
            LocalBuildCatalog catalog = await _updates
                .LoadAsync(cancellationToken)
                .ConfigureAwait(true);
            if (!IsDisposed)
            {
                ApplyCatalog(catalog);
            }
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("빌드 폴더를 변경하지 못했습니다.", exception);
        }
        finally
        {
            EndOperation();
        }
    }

    internal async Task InstallSelectedBuildAsync()
    {
        LocalBuildItemViewModel? selectedBuild = SelectedBuild;
        if (selectedBuild?.CanInstall != true || !TryBeginOperation())
        {
            return;
        }

        try
        {
            if (!_confirmInstall(selectedBuild))
            {
                return;
            }

            ErrorMessage = null;
            bool started = await _updates
                .InstallAsync(selectedBuild.BuildId, _lifetimeCancellationSource.Token)
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            StatusMessage = started
                ? "Windows에 설치 실행을 요청했습니다. 관리자 권한 요청과 설치 창을 확인하십시오. 설치가 진행되면 ZARA가 종료되고 업데이트 후 다시 시작됩니다."
                : "관리자 권한 요청이 취소되어 업데이트를 시작하지 않았습니다.";
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("업데이트를 시작하지 못했습니다.", exception);
        }
        finally
        {
            EndOperation();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _lifetimeCancellationSource.Cancel();
        _lifetimeCancellationSource.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task LoadAsync()
    {
        if (!TryBeginOperation())
        {
            return;
        }

        try
        {
            ErrorMessage = null;
            LocalBuildCatalog catalog = await _updates
                .LoadAsync(_lifetimeCancellationSource.Token)
                .ConfigureAwait(true);
            if (!IsDisposed)
            {
                ApplyCatalog(catalog);
            }
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
        }
        catch (Exception exception)
        {
            ReportFailure("빌드 목록을 불러오지 못했습니다.", exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private void ApplyCatalog(LocalBuildCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        string? currentBuildId = catalog.CurrentBuild?.BuildId;
        Builds = catalog.Builds
            .Select(entry => new LocalBuildItemViewModel(
                entry,
                string.Equals(entry.Build.BuildId, currentBuildId, StringComparison.Ordinal)))
            .ToArray();
        _selectedBuild = null;
        OnPropertyChanged(nameof(SelectedBuild));
        OnPropertyChanged(nameof(HasSelectedBuild));
        BuildsDirectory = catalog.BuildsDirectory;
        CurrentBuildVersionConfiguration = catalog.CurrentBuild is { } current
            ? $"{current.VersionName} · {current.Configuration}"
            : "현재 빌드 정보를 확인할 수 없습니다.";
        CurrentBuildBranch = catalog.CurrentBuild?.Branch ?? string.Empty;
        CurrentBuildShortCommit = catalog.CurrentBuild?.Commit is { Length: >= 7 } commit
            ? commit[..7]
            : catalog.CurrentBuild?.Commit ?? string.Empty;
        ErrorMessage = null;
        StatusMessage = !string.IsNullOrWhiteSpace(catalog.Message)
            ? catalog.Message
            : Builds.Count == 0
                ? "설치할 수 있는 로컬 빌드가 없습니다."
                : $"로컬 빌드 {Builds.Count}개를 불러왔습니다.";
        OnPropertyChanged(nameof(CanInstallSelected));
        _installSelectedBuildCommand.NotifyCanExecuteChanged();
    }

    private bool TryBeginOperation()
    {
        if (IsDisposed || Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            return false;
        }

        if (IsDisposed)
        {
            Interlocked.Exchange(ref _operationInProgress, 0);
            return false;
        }

        RaiseBusyStateChanged();
        return true;
    }

    private void EndOperation()
    {
        if (Interlocked.Exchange(ref _operationInProgress, 0) != 0 && !IsDisposed)
        {
            RaiseBusyStateChanged();
        }
    }

    private bool CanStartOperation() => CanInteract;

    private bool CanInstallSelectedBuild() =>
        CanInteract && SelectedBuild?.CanInstall == true;

    private void RaiseBusyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanInstallSelected));
        _refreshCommand.NotifyCanExecuteChanged();
        _changeDirectoryCommand.NotifyCanExecuteChanged();
        _installSelectedBuildCommand.NotifyCanExecuteChanged();
    }

    private void ReportUnexpectedFailure(Exception exception) =>
        ReportFailure("빌드 작업을 처리하지 못했습니다.", exception);

    private void ReportFailure(string message, Exception exception)
    {
        if (IsDisposed)
        {
            return;
        }

        ErrorMessage = string.IsNullOrWhiteSpace(exception.Message)
            ? message
            : $"{message} {exception.Message}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
#endif
