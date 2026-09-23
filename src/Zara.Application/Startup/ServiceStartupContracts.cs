namespace Zara.Application.Startup;

public enum StartupServiceState
{
    Stopped,
    StartPending,
    StopPending,
    Running,
    Paused,
    Other
}

public sealed record ServiceStartupStatus(
    StartupServiceState State,
    uint Win32ExitCode = 0,
    uint ServiceExitCode = 0,
    bool StartDisabled = false);

public interface IServiceStartupPort
{
    ServiceStartupStatus ReadStatus();

    Task StartAsync(bool elevated, CancellationToken cancellationToken);
}

public enum StartupFailureKind
{
    AccessDenied,
    ElevationCancelled,
    ServiceMissing,
    BinaryMissing,
    ServiceDisabled,
    ServiceDeleting,
    IdentityMismatch,
    StartFailed,
    ServiceStopped,
    TimedOut,
    ConnectionFailed,
    RegistrationRejected,
    Unexpected
}

public sealed class DesktopStartupException : Exception
{
    public DesktopStartupException(
        StartupFailureKind kind,
        string message,
        int? nativeErrorCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        NativeErrorCode = nativeErrorCode;
    }

    public StartupFailureKind Kind { get; }

    public int? NativeErrorCode { get; }

    public uint? ServiceExitCode { get; init; }
}
