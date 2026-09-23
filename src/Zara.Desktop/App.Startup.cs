using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Zara.Application.Startup;
using Zara.Core.Continuity;
using Zara.Infrastructure.Windows;
using Zara.Infrastructure.Windows.Continuity;
using Zara.Infrastructure.Windows.Startup;
using Zara.Supervision.Contracts;

namespace Zara.Desktop;

public partial class App : IDesktopStartupPort
{
    private readonly TaskCompletionSource _startupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WindowsDesktopActivationChannel? _startupActivation;
    private WindowsDesktopStartupGate? _startupGate;
    private CancellationTokenSource? _startupCancellation;
    private StartupWindow? _startupWindow;

    internal void SetStartupGate(WindowsDesktopStartupGate? gate) => _startupGate = gate;

    private async Task<bool> EstablishSupervisionAsync(string? launchToken)
    {
        if (launchToken is not null)
        {
            _startupActivation = WindowsDesktopActivationChannel.AcquirePrimary();
            AttachActivationChannel(_startupActivation);
            await ConnectSupervisionAsync(launchToken, CancellationToken.None).ConfigureAwait(true);
            return true;
        }

        _startupCancellation = new CancellationTokenSource();
        _startupWindow = new StartupWindow(_startupCancellation);
        // Keep normal launches unobtrusive; display progress if preparation takes noticeable time.
        Task showProgress = ShowStartupProgressAsync(_startupCancellation.Token);
        var useCase = new DesktopStartupUseCase(new WindowsServiceStartupPort(), this,
            token => Dispatcher.InvokeAsync(() => _startupWindow.ConfirmElevationAsync(token)).Task.Unwrap());
        try
        {
            while (true)
            {
                try
                {
                    DesktopStartupOutcome result = await useCase.PrepareAsync(_startupCancellation.Token).ConfigureAwait(true);
                    if (result == DesktopStartupOutcome.ActivatedExisting)
                    {
                        CompleteStartupPresentation();
                        return false;
                    }
                    _startupCancellation.Token.ThrowIfCancellationRequested();
                    CloseStartupWindow();
                    return true;
                }
                catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Trace.TraceError("ZARA startup preparation failed: {0}", exception);
                    await ReleaseStartupConnectionAsync().ConfigureAwait(true);
                    ReleaseStartupActivation();
                    EnsureStartupWindowVisible();
                    bool retry = await _startupWindow.OfferRetryAsync(
                        StartupFailurePresentation.FromException(exception), _startupCancellation.Token).ConfigureAwait(true);
                    if (!retry)
                    {
                        throw new OperationCanceledException(_startupCancellation.Token);
                    }
                }
            }
        }
        finally
        {
            // The delayed presentation only observes cancellation; it never owns process lifetime.
            _ = showProgress;
        }
    }

    private async Task ShowStartupProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(true);
            EnsureStartupWindowVisible();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void EnsureStartupWindowVisible()
    {
        if (_startupWindow is { IsVisible: false } && !_startupCancellation!.IsCancellationRequested)
        {
            _startupWindow.Show();
        }
    }

    Task<bool> IDesktopStartupPort.TryActivateAsync(CancellationToken cancellationToken) =>
        TryActivateForStartupAsync(cancellationToken);

    private static async Task<bool> TryActivateForStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await WindowsDesktopActivationChannel.TryActivateExistingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new DesktopStartupException(StartupFailureKind.IdentityMismatch,
                "기존 ZARA 프로세스 정보를 확인하지 못했습니다.", innerException: exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DesktopStartupException(StartupFailureKind.ConnectionFailed,
                "기존 ZARA 프로세스가 아직 준비되지 않았습니다.", innerException: exception);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            throw new DesktopStartupException(StartupFailureKind.ConnectionFailed,
                "기존 ZARA 프로세스의 준비 완료를 확인하지 못했습니다.", innerException: exception);
        }
    }

    Task<DesktopStartupOutcome> IDesktopStartupPort.ConnectAsync(CancellationToken cancellationToken) =>
        Dispatcher.InvokeAsync(() => ConnectManualDesktopAsync(cancellationToken)).Task.Unwrap();

    private async Task<DesktopStartupOutcome> ConnectManualDesktopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _startupActivation = await WindowsDesktopActivationChannel.AcquireOrActivateAsync(cancellationToken).ConfigureAwait(true);
            if (_startupActivation is null)
            {
                return DesktopStartupOutcome.ActivatedExisting;
            }
            AttachActivationChannel(_startupActivation);
            await ConnectSupervisionAsync(null, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            return DesktopStartupOutcome.ReadyHere;
        }
        catch (SupervisionRegistrationException exception) when (exception.RequiresServiceDesktop)
        {
            ReleaseStartupActivation();
            return DesktopStartupOutcome.WaitForServiceDesktop;
        }
        catch (Exception exception)
        {
            await ReleaseStartupConnectionAsync().ConfigureAwait(true);
            ReleaseStartupActivation();
            if (exception is SupervisionRegistrationException)
            {
                throw new DesktopStartupException(StartupFailureKind.RegistrationRejected,
                    "서비스가 연결 등록을 거부했습니다.", innerException: exception);
            }
            throw;
        }
    }

    private async Task ConnectSupervisionAsync(string? launchToken, CancellationToken cancellationToken)
    {
        _settingsStore ??= new WindowsDesktopRestartSettingsStore();
        _restartSettings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
        RestartContinuityDecision decision = RestartContinuityPolicy.Decide(false, _restartSettings.RestartOnExitWhenUnlocked);
        try
        {
            _supervisionConnection = await WindowsSupervisionConnection.ConnectAsync(launchToken,
                new SupervisionLease(0, decision.RestartRequired, decision.RecoverLock), cancellationToken).ConfigureAwait(true);
        }
        catch (InvalidDataException exception)
        {
            throw new DesktopStartupException(StartupFailureKind.IdentityMismatch,
                "서비스 연결 정보를 확인하지 못했습니다.", innerException: exception);
        }
        catch (Exception exception) when (exception is IOException or Win32Exception ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DesktopStartupException(StartupFailureKind.ConnectionFailed,
                "서비스와 연결하지 못했습니다.", (exception as Win32Exception)?.NativeErrorCode, exception);
        }
    }

    private async Task ReleaseStartupConnectionAsync()
    {
        if (_supervisionConnection is { } connection)
        {
            _supervisionConnection = null;
            await connection.DisposeAsync().ConfigureAwait(true);
        }
    }
    private void ReleaseStartupActivation()
    {
        _startupActivation?.Dispose();
        _startupActivation = null;
    }

    private void CompleteStartupPresentation()
    {
        CloseStartupWindow();
        _startupGate?.Dispose();
        _startupGate = null;
    }

    private void CloseStartupWindow()
    {
        _startupWindow?.Complete();
        _startupWindow = null;
    }

    private void DisposeStartupResources()
    {
        _startupCancellation?.Cancel();
        _startupReady.TrySetCanceled();
        CompleteStartupPresentation();
        ReleaseStartupActivation();
        _startupCancellation?.Dispose();
        _startupCancellation = null;
    }
}
