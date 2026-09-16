using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Zara.Enforcement.Service.Windows;
using Zara.Supervision.Contracts;

namespace Zara.Enforcement.Service.Supervision;

/// <summary>
/// Accepts one authenticated desktop generation at a time and translates its acknowledged lease
/// into launch-needed directives without moving product policy into the Service.
/// </summary>
internal sealed class WindowsSupervisionPipeServer
{
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LatestSupervisionCommandSource _commandSource;
    private readonly DesktopLaunchHandshakeRegistry _handshakeRegistry;
    private readonly int _targetSessionId;
    private readonly SecurityIdentifier _targetUserSid;
    private readonly string _desktopExecutablePath;
    private readonly ITaskManagerRestriction? _taskManagerRestriction;

    // The boot-time Service must not let a tokenless desktop claim the first generation before the
    // exact child created with CreateProcessAsUser has registered and reported healthy.
    private bool _initialServiceLaunchRequired;
    private SupervisionLease? _lastAcceptedLease;

    public WindowsSupervisionPipeServer(
        LatestSupervisionCommandSource commandSource,
        DesktopLaunchHandshakeRegistry handshakeRegistry,
        int targetSessionId,
        SecurityIdentifier targetUserSid,
        string? installDirectory = null,
        bool requireInitialServiceLaunch = false,
        ITaskManagerRestriction? taskManagerRestriction = null)
    {
        ArgumentNullException.ThrowIfNull(commandSource);
        ArgumentNullException.ThrowIfNull(handshakeRegistry);
        ArgumentNullException.ThrowIfNull(targetUserSid);
        ArgumentOutOfRangeException.ThrowIfNegative(targetSessionId);

        _commandSource = commandSource;
        _handshakeRegistry = handshakeRegistry;
        _targetSessionId = targetSessionId;
        _targetUserSid = targetUserSid;
        _initialServiceLaunchRequired = requireInitialServiceLaunch;
        _taskManagerRestriction = taskManagerRestriction;
        string directory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installDirectory ?? AppContext.BaseDirectory));
        _desktopExecutablePath = Path.GetFullPath(
            Path.Combine(directory, "Zara.Desktop.exe"));
    }

    /// <summary>
    /// Listens until Service cancellation. A malformed or unauthenticated client is isolated to
    /// its own pipe instance and cannot replace the last acknowledged lease.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using NamedPipeServerStream pipe = CreatePipe();
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

#pragma warning disable CA1031 // One invalid local client must not terminate the Service listener.
            try
            {
                await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace.TraceError("The supervision pipe client was rejected: {0}", exception);
            }
#pragma warning restore CA1031
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken serviceCancellationToken)
    {
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };

        using var registrationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            serviceCancellationToken);
        registrationCancellation.CancelAfter(RegistrationTimeout);
        SupervisionRequest registration = await ReadMessageAsync<SupervisionRequest>(
                reader,
                registrationCancellation.Token)
            .ConfigureAwait(false);
        ValidateRegistrationShape(registration);

        using Process clientProcess = AuthenticateClient(pipe, registration.Process!);
        var lifetime = new ProcessDesktopLifetime(clientProcess);
        var channel = new StreamSupervisionMessageChannel(reader, writer);
        await HandleAuthenticatedConnectionAsync(
                channel,
                lifetime,
                registration,
                serviceCancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the authenticated connection state machine independently of pipe transport details.
    /// This boundary makes response-write and exact-process-exit races deterministic in tests.
    /// </summary>
    internal async Task HandleAuthenticatedConnectionAsync(
        ISupervisionMessageChannel channel,
        IAuthenticatedDesktopLifetime clientProcess,
        SupervisionRequest registration,
        CancellationToken serviceCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(clientProcess);
        ValidateRegistrationShape(registration);

        int processId = clientProcess.ProcessId;
        bool serviceLaunch = registration.LaunchToken is not null;
        bool initialServiceLaunch = _initialServiceLaunchRequired;
        if (_commandSource.IsSessionEnding)
        {
            await WriteRejectedAsync(channel, "SESSION_ENDED", serviceCancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (serviceLaunch)
        {
            if (!_handshakeRegistry.IsPendingForProcess(
                    registration.LaunchToken!,
                    _targetSessionId,
                    processId) ||
                (_lastAcceptedLease is null && !initialServiceLaunch))
            {
                await WriteRejectedAsync(channel, "INVALID_LAUNCH_TOKEN", serviceCancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (_lastAcceptedLease is null)
            {
                _lastAcceptedLease = registration.Lease;
            }
        }
        else if (initialServiceLaunch)
        {
            await WriteRejectedAsync(
                    channel,
                    "SERVICE_LAUNCH_TOKEN_REQUIRED",
                    serviceCancellationToken)
                .ConfigureAwait(false);
            return;
        }
        else if (_lastAcceptedLease?.RestartRequiredAfterExit == true &&
                 _commandSource.Current.RestartRequired)
        {
            await WriteRejectedAsync(channel, "RECOVERY_TOKEN_REQUIRED", serviceCancellationToken)
                .ConfigureAwait(false);
            return;
        }
        else
        {
            _lastAcceptedLease = registration.Lease;
        }

        long generationRevision = 0;
        SupervisionLease? preparedLease = null;
        bool releaseAcknowledged = false;
        SupervisionLease? preparedRelease = null;

        try
        {
            _commandSource.PublishNext(
                restartRequired: false,
                SupervisionDirectiveReason.LeaseUpdated);
            await WriteResponseAsync(
                    channel,
                    new SupervisionResponse(
                        SupervisionProtocol.CurrentVersion,
                        SupervisionResponseKind.Registered,
                        AcknowledgedRevision: serviceLaunch ? 0 : registration.Lease!.Revision,
                        RecoverLockOnStart: serviceLaunch &&
                            _lastAcceptedLease!.RecoverLockOnRestart,
                        ErrorCode: null),
                    serviceCancellationToken)
                .ConfigureAwait(false);

            while (true)
            {
                SupervisionRequest? request = await channel
                    .ReadAsync(serviceCancellationToken)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                ValidateCommonRequest(request);
                if (preparedLease is not null &&
                    request.Kind != SupervisionRequestKind.CommitLease)
                {
                    throw new InvalidDataException(
                        "A prepared lease requires its ordered commit confirmation.");
                }

                switch (request.Kind)
                {
                    case SupervisionRequestKind.RestrictTaskManager:
                    case SupervisionRequestKind.RestoreTaskManager:
                        ValidateTaskManagerRequest(request);
                        await ApplyTaskManagerRequestAsync(
                                channel,
                                request.Kind,
                                registration.Process!,
                                serviceCancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case SupervisionRequestKind.UpdateLease:
                        ValidateLeaseRequest(request, generationRevision);
                        SupervisionLease requestedLease = request.Lease!;
                        bool reducesProtection =
                            (_lastAcceptedLease?.RestartRequiredAfterExit == true &&
                             !requestedLease.RestartRequiredAfterExit) ||
                            (_lastAcceptedLease?.RecoverLockOnRestart == true &&
                             !requestedLease.RecoverLockOnRestart);
                        if (!reducesProtection)
                        {
                            _lastAcceptedLease = requestedLease;
                            generationRevision = requestedLease.Revision;
                            await WriteAcknowledgementAsync(
                                    channel,
                                    SupervisionResponseKind.LeaseAcknowledged,
                                    requestedLease.Revision,
                                    serviceCancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteAcknowledgementAsync(
                                    channel,
                                    SupervisionResponseKind.LeasePrepared,
                                    requestedLease.Revision,
                                    serviceCancellationToken)
                                .ConfigureAwait(false);
                            preparedLease = requestedLease;
                        }

                        break;

                    case SupervisionRequestKind.CommitLease:
                        ValidateCommitLeaseRequest(request, preparedLease);
                        generationRevision = preparedLease!.Revision;
                        _lastAcceptedLease = preparedLease;
                        preparedLease = null;
                        break;

                    case SupervisionRequestKind.ReportHealthy:
                        ValidateRevisionRequest(request, generationRevision);
                        if (serviceLaunch &&
                            !_handshakeRegistry.TryReportHealthy(
                                registration.LaunchToken!,
                                _targetSessionId,
                                processId))
                        {
                            await WriteRejectedAsync(
                                    channel,
                                    "HEALTH_TOKEN_REJECTED",
                                    serviceCancellationToken)
                                .ConfigureAwait(false);
                            return;
                        }

                        await WriteAcknowledgementAsync(
                                channel,
                                SupervisionResponseKind.HealthyAcknowledged,
                                generationRevision,
                                serviceCancellationToken)
                            .ConfigureAwait(false);
                        if (initialServiceLaunch)
                        {
                            _initialServiceLaunchRequired = false;
                        }

                        serviceLaunch = false;
                        break;

                    case SupervisionRequestKind.ReleaseForExplicitExit:
                        ValidateReleaseRequest(request, generationRevision);
                        SupervisionLease release = request.Lease!;
                        preparedRelease = release;
                        await WriteAcknowledgementAsync(
                                channel,
                                SupervisionResponseKind.ExitAcknowledged,
                                release.Revision,
                                serviceCancellationToken)
                            .ConfigureAwait(false);
                        generationRevision = release.Revision;
                        releaseAcknowledged = true;
                        return;

                    default:
                        await WriteRejectedAsync(
                                channel,
                                "INVALID_REQUEST_KIND",
                                serviceCancellationToken)
                            .ConfigureAwait(false);
                        return;
                }
            }
        }
        finally
        {
            try
            {
                if (!serviceCancellationToken.IsCancellationRequested)
                {
                    await clientProcess.WaitForExitAsync(serviceCancellationToken)
                        .ConfigureAwait(false);
                    bool explicitExitCompleted =
                        releaseAcknowledged &&
                        preparedRelease is not null &&
                        clientProcess.ExitCode == SupervisionProtocol.ExplicitExitCode;
                    if (explicitExitCompleted)
                    {
                        _lastAcceptedLease = preparedRelease;
                    }

                    bool restartRequired =
                        !explicitExitCompleted &&
                        (_initialServiceLaunchRequired ||
                         _lastAcceptedLease?.RestartRequiredAfterExit == true);
                    _commandSource.PublishNext(
                        restartRequired,
                        explicitExitCompleted
                            ? SupervisionDirectiveReason.ExplicitRelease
                            : SupervisionDirectiveReason.LeaseUpdated);
                }
            }
            finally
            {
                _taskManagerRestriction?.Restore();
            }
        }
    }

    private async Task ApplyTaskManagerRequestAsync(
        ISupervisionMessageChannel channel,
        SupervisionRequestKind kind,
        SupervisedProcessIdentity process,
        CancellationToken cancellationToken)
    {
        try
        {
            ITaskManagerRestriction restriction = _taskManagerRestriction ??
                throw new InvalidOperationException("Task Manager restriction is unavailable.");
            if (kind == SupervisionRequestKind.RestrictTaskManager)
            {
                restriction.Apply(process);
            }
            else
            {
                restriction.Restore();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            SecurityException or Win32Exception or InvalidOperationException or AggregateException)
        {
            Trace.TraceError("The Task Manager policy request failed: {0}", exception);
            await WriteRejectedAsync(channel, "TASK_MANAGER_POLICY_FAILED", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteAcknowledgementAsync(
                channel,
                kind == SupervisionRequestKind.RestrictTaskManager
                    ? SupervisionResponseKind.TaskManagerRestricted
                    : SupervisionResponseKind.TaskManagerRestored,
                revision: 0,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateTaskManagerRequest(SupervisionRequest request)
    {
        if (request.Process is not null || request.Lease is not null ||
            request.Revision is not null || request.LaunchToken is not null)
        {
            throw new InvalidDataException("The Task Manager request shape was invalid.");
        }
    }

    private Process AuthenticateClient(
        NamedPipeServerStream pipe,
        SupervisedProcessIdentity claimedIdentity)
    {
        if (!NativeMethods.GetNamedPipeClientProcessId(
                pipe.SafePipeHandle,
                out uint clientProcessIdValue) ||
            clientProcessIdValue == 0 ||
            clientProcessIdValue > int.MaxValue ||
            claimedIdentity.ProcessId != (int)clientProcessIdValue)
        {
            throw new InvalidDataException("The pipe client process identity did not match.");
        }

        Process process = Process.GetProcessById((int)clientProcessIdValue);
        try
        {
            if (process.HasExited ||
                process.SessionId != _targetSessionId ||
                claimedIdentity.SessionId != _targetSessionId ||
                process.StartTime.ToUniversalTime().Ticks !=
                    claimedIdentity.ProcessStartTimeUtcTicks)
            {
                throw new InvalidDataException("The desktop process lifetime did not match.");
            }

            string processPath = Path.GetFullPath(
                process.MainModule?.FileName ?? string.Empty);
            if (!string.Equals(
                    processPath,
                    _desktopExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The desktop executable path did not match.");
            }

            string actualSid = GetProcessUserSid(process);
            if (!string.Equals(actualSid, claimedIdentity.UserSid, StringComparison.Ordinal) ||
                !string.Equals(actualSid, _targetUserSid.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The desktop user identity did not match.");
            }

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static string GetProcessUserSid(Process process)
    {
        const uint TokenQuery = 0x0008;
        if (!NativeMethods.OpenProcessToken(
                process.Handle,
                TokenQuery,
                out nint tokenValue))
        {
            throw new InvalidOperationException("The desktop process token could not be opened.");
        }

        using var token = new SafeAccessTokenHandle(tokenValue);
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        return identity.User?.Value ??
            throw new InvalidOperationException("The desktop process did not have a user SID.");
    }

    private NamedPipeServerStream CreatePipe()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            _targetUserSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            SupervisionProtocol.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
                PipeOptions.WriteThrough |
                PipeOptions.FirstPipeInstance,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity,
            HandleInheritability.None,
            additionalAccessRights: 0);
    }

    private static void ValidateRegistrationShape(SupervisionRequest request)
    {
        ValidateCommonRequest(request);
        if (request.Kind != SupervisionRequestKind.Register ||
            request.Process is null ||
            request.Lease is not { Revision: 0 } ||
            request.Revision is not null ||
            (request.LaunchToken is not null &&
             (request.LaunchToken.Length != 64 ||
              !request.LaunchToken.All(char.IsAsciiHexDigit))))
        {
            throw new InvalidDataException("The registration request shape was invalid.");
        }
    }

    private static void ValidateLeaseRequest(SupervisionRequest request, long currentRevision)
    {
        if (request.Process is not null ||
            request.Lease is null ||
            request.Lease.Revision <= currentRevision ||
            request.Revision is not null ||
            request.LaunchToken is not null)
        {
            throw new InvalidDataException("The lease request shape was invalid.");
        }
    }

    private static void ValidateRevisionRequest(SupervisionRequest request, long currentRevision)
    {
        if (request.Process is not null ||
            request.Lease is not null ||
            request.Revision != currentRevision ||
            currentRevision <= 0 ||
            request.LaunchToken is not null)
        {
            throw new InvalidDataException("The health request shape was invalid.");
        }
    }

    private static void ValidateCommitLeaseRequest(
        SupervisionRequest request,
        SupervisionLease? preparedLease)
    {
        if (preparedLease is null ||
            request.Process is not null ||
            request.Lease is not null ||
            request.Revision != preparedLease.Revision ||
            request.LaunchToken is not null)
        {
            throw new InvalidDataException("The lease commit request shape was invalid.");
        }
    }

    private static void ValidateReleaseRequest(SupervisionRequest request, long currentRevision)
    {
        ValidateLeaseRequest(request, currentRevision);
        if (request.Lease is not
            {
                RestartRequiredAfterExit: false,
                RecoverLockOnRestart: false,
            })
        {
            throw new InvalidDataException("The release request did not revoke supervision.");
        }
    }

    private static void ValidateCommonRequest(SupervisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != SupervisionProtocol.CurrentVersion)
        {
            throw new InvalidDataException("The supervision protocol version was invalid.");
        }
    }

    private static Task WriteAcknowledgementAsync(
        ISupervisionMessageChannel channel,
        SupervisionResponseKind kind,
        long revision,
        CancellationToken cancellationToken) =>
        WriteResponseAsync(
            channel,
            new SupervisionResponse(
                SupervisionProtocol.CurrentVersion,
                kind,
                revision,
                RecoverLockOnStart: false,
                ErrorCode: null),
            cancellationToken);

    private static Task WriteRejectedAsync(
        ISupervisionMessageChannel channel,
        string errorCode,
        CancellationToken cancellationToken) =>
        WriteResponseAsync(
            channel,
            new SupervisionResponse(
                SupervisionProtocol.CurrentVersion,
                SupervisionResponseKind.Rejected,
                AcknowledgedRevision: 0,
                RecoverLockOnStart: false,
                errorCode),
            cancellationToken);

    private static async Task WriteResponseAsync(
        ISupervisionMessageChannel channel,
        SupervisionResponse response,
        CancellationToken cancellationToken)
    {
        await channel.WriteAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadMessageAsync<T>(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        T? message = await TryReadMessageAsync<T>(reader, cancellationToken).ConfigureAwait(false);
        return message ?? throw new EndOfStreamException(
            "The supervision channel closed before registration.");
    }

    private static async Task<T?> TryReadMessageAsync<T>(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(capacity: 256);
        var buffer = new char[1];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return builder.Length == 0
                    ? default
                    : throw new EndOfStreamException(
                        "The supervision request ended before its line delimiter.");
            }

            if (buffer[0] == '\n')
            {
                break;
            }

            if (buffer[0] != '\r')
            {
                builder.Append(buffer[0]);
                if (builder.Length > SupervisionProtocol.MaximumMessageCharacters)
                {
                    throw new InvalidDataException(
                        "The supervision request exceeded its size limit.");
                }
            }
        }

        return JsonSerializer.Deserialize<T>(builder.ToString(), JsonOptions) ??
            throw new InvalidDataException("The supervision request was empty or invalid.");
    }

    private sealed class StreamSupervisionMessageChannel : ISupervisionMessageChannel
    {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public StreamSupervisionMessageChannel(StreamReader reader, StreamWriter writer)
        {
            _reader = reader;
            _writer = writer;
        }

        public ValueTask<SupervisionRequest?> ReadAsync(CancellationToken cancellationToken) =>
            new(TryReadMessageAsync<SupervisionRequest>(_reader, cancellationToken));

        public async ValueTask WriteAsync(
            SupervisionResponse response,
            CancellationToken cancellationToken)
        {
            string serialized = JsonSerializer.Serialize(response, JsonOptions);
            if (serialized.Length > SupervisionProtocol.MaximumMessageCharacters)
            {
                throw new InvalidOperationException(
                    "The supervision response exceeded its size limit.");
            }

            await _writer.WriteLineAsync(serialized.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class ProcessDesktopLifetime : IAuthenticatedDesktopLifetime
    {
        private readonly Process _process;

        public ProcessDesktopLifetime(Process process)
        {
            _process = process;
        }

        public int ProcessId => _process.Id;

        public int ExitCode => _process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.HasExited
                ? Task.CompletedTask
                : _process.WaitForExitAsync(cancellationToken);
    }
}

/// <summary>
/// Exchanges bounded supervision requests and responses after the pipe client was authenticated.
/// </summary>
internal interface ISupervisionMessageChannel
{
    ValueTask<SupervisionRequest?> ReadAsync(CancellationToken cancellationToken);

    ValueTask WriteAsync(
        SupervisionResponse response,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exposes only the exact authenticated process lifetime needed by the supervision state machine.
/// </summary>
internal interface IAuthenticatedDesktopLifetime
{
    int ProcessId { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);
}
