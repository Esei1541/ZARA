using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Zara.Application.Updates;
using Zara.Desktop.Commands;

namespace Zara.Desktop.Updates;

/// <summary>Shares one update state between the startup notification and the update tab.</summary>
internal sealed class ReleaseUpdateViewModel : INotifyPropertyChanged, IDisposable
{
    internal const string UpdatePrompt = "신규 업데이트가 있습니다.\n바로 설치할까요?";
    private readonly IReleaseUpdates _updates;
    private readonly Func<bool> _confirmInstall;
    private readonly Func<bool> _canShowStartupPrompt;
    private readonly Action _showUpdateTab;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncCommand _refreshCommand;
    private readonly AsyncCommand _installCommand;
    private Task? _checkTask;
    private ReleaseUpdate? _release;
    private bool _busy;
    private bool _prompting;
    private bool _startupChecked;
    private bool _startupPromptPending;
    private bool _disposed;

    internal ReleaseUpdateViewModel(
        IReleaseUpdates updates,
        Func<bool> confirmInstall,
        Func<bool> canShowStartupPrompt,
        Action showUpdateTab)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _confirmInstall = confirmInstall ?? throw new ArgumentNullException(nameof(confirmInstall));
        _canShowStartupPrompt = canShowStartupPrompt ?? throw new ArgumentNullException(nameof(canShowStartupPrompt));
        _showUpdateTab = showUpdateTab ?? throw new ArgumentNullException(nameof(showUpdateTab));
        _refreshCommand = new AsyncCommand(RefreshAsync, ReportUnexpectedFailure, () => CanInteract);
        _installCommand = new AsyncCommand(() => InstallAsync(), ReportUnexpectedFailure, () => CanInstall);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentVersion => $"v{_updates.CurrentVersion.ToString(3)}";
    public string LatestVersion => _release is null ? string.Empty : $"v{_release.Version.ToString(3)}";
    public string ReleaseNotes => _release?.Notes ?? string.Empty;
    public string StatusMessage { get; private set; } = "업데이트를 확인합니다.";
    public string ErrorCode { get; private set; } = string.Empty;
    public string ErrorDescription => DescribeError(ErrorCode);
    public bool HasError => ErrorCode.Length != 0;
    public string StatusGlyph => HasError ? "\uEA39" : HasUpdate ? "\uE896" : _busy ? "\uE895" : "\uE73E";
    public bool HasUpdate => _release is not null && _release.Version > _updates.CurrentVersion;
    public bool CanInteract => !_busy && !_prompting && !_disposed;
    public bool CanInstall => HasUpdate && CanInteract;
    public bool IsDownloading { get; private set; }
    public int DownloadProgress { get; private set; }
    public string OperationMessage { get; private set; } = string.Empty;
    public bool HasOperationMessage => OperationMessage.Length != 0;
    public ICommand RefreshCommand => _refreshCommand;
    public ICommand InstallCommand => _installCommand;

    internal Task RefreshAsync()
    {
        if (_disposed || IsDownloading || _prompting)
        {
            return Task.CompletedTask;
        }

        if (_checkTask is { IsCompleted: false })
        {
            return _checkTask;
        }

        _checkTask = CheckCoreAsync();
        return _checkTask;
    }

    internal async Task CheckOnStartupAsync()
    {
        if (_startupChecked || _disposed)
        {
            return;
        }

        _startupChecked = true;
        await RefreshAsync().ConfigureAwait(true);
        _startupPromptPending = HasUpdate && !HasError;
        await TryShowStartupPromptAsync().ConfigureAwait(true);
    }

    internal async Task TryShowStartupPromptAsync()
    {
        if (!_startupPromptPending || !CanInstall || !_canShowStartupPrompt())
        {
            return;
        }

        _startupPromptPending = false;
        await InstallAsync(showTab: true).ConfigureAwait(true);
    }

    internal async Task InstallAsync(bool showTab = false)
    {
        if (!CanInstall || _release is not { } release)
        {
            return;
        }

        _prompting = true;
        NotifyState();
        try
        {
            if (!_confirmInstall() || _disposed)
            {
                return;
            }

            if (showTab)
            {
                _showUpdateTab();
            }

            _busy = true;
            IsDownloading = true;
            DownloadProgress = 0;
            ErrorCode = string.Empty;
            OperationMessage = "업데이트를 다운로드하고 있습니다.";
            NotifyState();
            var progress = new Progress<int>(value =>
            {
                if (!_disposed && IsDownloading)
                {
                    DownloadProgress = value;
                    NotifyState();
                }
            });
            bool requested = await _updates.InstallAsync(release, progress, _lifetime.Token).ConfigureAwait(true);
            if (!_disposed)
            {
                OperationMessage = requested ? "Windows에 설치 실행을 요청했습니다." : "설치를 취소했습니다.";
            }
        }
        catch (OperationCanceledException) when (_disposed || _lifetime.IsCancellationRequested)
        {
            // App shutdown owns cancellation; no late UI or installer request should follow.
        }
        catch (Exception exception)
        {
            SetFailure(exception, installing: true);
        }
        finally
        {
            _prompting = false;
            _busy = false;
            IsDownloading = false;
            if (!_disposed)
            {
                NotifyState();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task CheckCoreAsync()
    {
        _busy = true;
        _release = null;
        ErrorCode = string.Empty;
        OperationMessage = string.Empty;
        StatusMessage = "업데이트를 확인하고 있습니다.";
        NotifyState();
        try
        {
            ReleaseUpdate release = await _updates.CheckAsync(_lifetime.Token).ConfigureAwait(true);
            if (!_disposed)
            {
                _release = release;
                StatusMessage = HasUpdate ? "설치 가능한 업데이트가 있습니다." : "최신 버전입니다.";
            }
        }
        catch (OperationCanceledException) when (_disposed || _lifetime.IsCancellationRequested)
        {
            // No failure notification is needed when the application is exiting.
        }
        catch (Exception exception)
        {
            SetFailure(exception, installing: false);
        }
        finally
        {
            _busy = false;
            if (!_disposed)
            {
                NotifyState();
            }
        }
    }

    private void SetFailure(Exception exception, bool installing)
    {
        if (_disposed)
        {
            return;
        }

        Trace.TraceError("ZARA update operation failed: {0}", exception);
        ErrorCode = exception is UpdateException update ? update.ErrorCode : "UPD-UNKNOWN";
        if (installing)
        {
            OperationMessage = "업데이트를 설치하지 못했습니다.";
        }
        else
        {
            StatusMessage = "지금은 업데이트를 확인할 수 없습니다.";
            _startupPromptPending = false;
        }
    }

    private void ReportUnexpectedFailure(Exception exception)
    {
        SetFailure(exception, installing: false);
        NotifyState();
    }

    private void NotifyState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        _refreshCommand.NotifyCanExecuteChanged();
        _installCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeError(string code) => code switch
    {
        "UPD-NET-OFFLINE" => "인터넷 연결 없음",
        "UPD-NET-CONNECT" => "연결 실패",
        "UPD-NET-TIMEOUT" => "응답 시간 초과",
        "UPD-GH-RATE-LIMIT" => "GitHub 요청 한도 초과",
        "UPD-GH-HTTP-404" => "Release 정보를 찾을 수 없음",
        "UPD-GH-INVALID-RESPONSE" => "Release 정보 형식 오류",
        "UPD-GH-ASSET-MISSING" => "설치파일을 찾을 수 없음",
        "UPD-DOWNLOAD-HASH" or "UPD-DOWNLOAD-SIZE" => "설치파일 검증 실패",
        "UPD-DOWNLOAD-FILE" => "설치파일 저장 실패",
        _ when code.StartsWith("UPD-GH-HTTP-5", StringComparison.Ordinal) => "GitHub 서버 오류",
        _ when code.StartsWith("UPD-GH-HTTP-", StringComparison.Ordinal) => "GitHub 요청 오류",
        _ => string.Empty,
    };
}
