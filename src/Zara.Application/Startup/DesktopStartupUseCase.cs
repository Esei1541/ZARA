namespace Zara.Application.Startup;

/// <summary>The process that will present the successfully initialized desktop.</summary>
public enum DesktopStartupOutcome
{
    ReadyHere,
    ActivatedExisting,
    WaitForServiceDesktop,
}

/// <summary>Owns desktop election and registration without exposing platform handles.</summary>
public interface IDesktopStartupPort
{
    Task<bool> TryActivateAsync(CancellationToken cancellationToken);
    Task<DesktopStartupOutcome> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>Prepares a manually launched desktop while preserving Service-owned first launches.</summary>
public sealed class DesktopStartupUseCase
{
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly IServiceStartupPort _service;
    private readonly IDesktopStartupPort _desktop;
    private readonly Func<CancellationToken, Task<bool>> _confirmElevation;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public DesktopStartupUseCase(
        IServiceStartupPort service,
        IDesktopStartupPort desktop,
        Func<CancellationToken, Task<bool>> confirmElevation,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _confirmElevation = confirmElevation ?? throw new ArgumentNullException(nameof(confirmElevation));
        _time = timeProvider ?? TimeProvider.System;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, _time, token));
    }

    public async Task<DesktopStartupOutcome> PrepareAsync(CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        bool startAttempted = false;
        bool waitForServiceDesktop = false;
        DesktopStartupException? lastConnectionFailure = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = PreparationTimeout - _time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                throw new DesktopStartupException(
                    StartupFailureKind.TimedOut, "서비스 또는 ZARA의 시작 준비 시간이 초과되었습니다.", innerException: lastConnectionFailure);
            }

            ServiceStartupStatus status = _service.ReadStatus();
            switch (status.State)
            {
                case StartupServiceState.Stopped:
                    if (startAttempted)
                    {
                        throw new DesktopStartupException(StartupFailureKind.ServiceStopped,
                            "서비스가 시작 중 종료되었습니다.", unchecked((int)status.Win32ExitCode))
                        { ServiceExitCode = status.ServiceExitCode };
                    }
                    if (status.StartDisabled)
                    {
                        throw new DesktopStartupException(StartupFailureKind.ServiceDisabled,
                            "ZARA 서비스가 사용 안 함으로 설정되어 있습니다.");
                    }
                    startAttempted = true;
                    waitForServiceDesktop = true;
                    try
                    {
                        await _service.StartAsync(elevated: false, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DesktopStartupException exception) when (exception.Kind == StartupFailureKind.AccessDenied)
                    {
                        if (!await _confirmElevation(cancellationToken).ConfigureAwait(false))
                        {
                            throw new DesktopStartupException(StartupFailureKind.ElevationCancelled,
                                "서비스 시작에 필요한 관리자 권한 요청을 취소했습니다.");
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        await _service.StartAsync(elevated: true, cancellationToken).ConfigureAwait(false);
                    }
                    // User consent and the native StartService call have their own lifetime.
                    started = _time.GetTimestamp();
                    break;
                case StartupServiceState.StartPending:
                    waitForServiceDesktop = true;
                    break;
                case StartupServiceState.StopPending:
                    break;
                case StartupServiceState.Running:
                    using (var attempt = new CancellationTokenSource(remaining, _time))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attempt.Token))
                    {
                        try
                        {
                            if (await _desktop.TryActivateAsync(linked.Token).ConfigureAwait(false))
                            {
                                return DesktopStartupOutcome.ActivatedExisting;
                            }
                            if (!waitForServiceDesktop)
                            {
                                DesktopStartupOutcome result = await _desktop.ConnectAsync(linked.Token).ConfigureAwait(false);
                                if (result != DesktopStartupOutcome.WaitForServiceDesktop)
                                {
                                    return result;
                                }
                                waitForServiceDesktop = true;
                            }
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new DesktopStartupException(StartupFailureKind.TimedOut,
                                "ZARA의 시작 준비 시간이 초과되었습니다.");
                        }
                        catch (DesktopStartupException exception) when (
                            exception.Kind is StartupFailureKind.ConnectionFailed or StartupFailureKind.TimedOut)
                        {
                            lastConnectionFailure = exception;
                        }
                    }
                    break;
                default:
                    throw new DesktopStartupException(StartupFailureKind.StartFailed,
                        "ZARA 서비스가 연결할 수 없는 상태입니다.");
            }
            await _delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
