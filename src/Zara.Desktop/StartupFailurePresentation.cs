using System.ComponentModel;
using System.IO;
using Zara.Application.Startup;

namespace Zara.Desktop;

/// <summary>Maps startup failures to actionable text without showing credentials or raw traces.</summary>
internal sealed record StartupFailurePresentation(string Message, string Details, bool CanRetry)
{
    internal static StartupFailurePresentation FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StartupFailureKind kind = exception is DesktopStartupException startup
            ? startup.Kind
            : exception switch
            {
                UnauthorizedAccessException => StartupFailureKind.AccessDenied,
                TimeoutException or OperationCanceledException => StartupFailureKind.TimedOut,
                _ => StartupFailureKind.Unexpected,
            };
        string message = kind switch
        {
            StartupFailureKind.AccessDenied => "ZARA 서비스를 시작할 권한을 얻지 못했습니다.",
            StartupFailureKind.ElevationCancelled => "관리자 권한 요청을 취소하여 ZARA를 시작하지 못했습니다.",
            StartupFailureKind.ServiceMissing => "설치된 ZARA 서비스를 찾을 수 없습니다. ZARA를 다시 설치해 주십시오.",
            StartupFailureKind.BinaryMissing => "ZARA 서비스 실행 파일을 찾을 수 없습니다. ZARA를 다시 설치해 주십시오.",
            StartupFailureKind.ServiceDisabled => "ZARA 서비스가 사용 안 함으로 설정되어 있습니다. 서비스 설정을 확인해 주십시오.",
            StartupFailureKind.ServiceDeleting => "ZARA 서비스가 제거 중입니다. 설치 또는 제거 작업이 끝난 뒤 다시 실행해 주십시오.",
            StartupFailureKind.IdentityMismatch => "설치된 ZARA 서비스 정보가 일치하지 않아 연결을 중단했습니다. 설치 상태를 확인해 주십시오.",
            StartupFailureKind.StartFailed => "ZARA 서비스를 시작하지 못했습니다.",
            StartupFailureKind.ServiceStopped => "ZARA 서비스가 시작 중 종료되었습니다.",
            StartupFailureKind.TimedOut => "정해진 시간 안에 ZARA의 시작 준비가 끝나지 않았습니다.",
            StartupFailureKind.ConnectionFailed => "ZARA 서비스와 연결하지 못했습니다.",
            StartupFailureKind.RegistrationRejected => "ZARA 서비스가 앱의 연결 등록을 거부했습니다.",
            _ => "ZARA를 시작하는 중 오류가 발생했습니다.",
        };
        int? native = (exception as DesktopStartupException)?.NativeErrorCode ??
            (exception as Win32Exception)?.NativeErrorCode;
        string details = $"오류 구분: {kind}" + (native is null ? string.Empty : $"\nWindows 오류 코드: {native}");
        if (exception is DesktopStartupException { ServiceExitCode: { } serviceExitCode })
        {
            details += $"\n서비스 오류 코드: {serviceExitCode}";
        }
        bool retry = kind is StartupFailureKind.StartFailed or StartupFailureKind.ServiceStopped or
            StartupFailureKind.TimedOut or StartupFailureKind.ConnectionFailed or StartupFailureKind.AccessDenied;
        return new(message, details, retry);
    }
}
