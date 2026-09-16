using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Zara.Application.Continuity;
using Zara.Application.Locking;
using Zara.Supervision.Contracts;

namespace Zara.Infrastructure.Windows.Continuity;

/// <summary>
/// Maintains one authenticated desktop-to-Service supervision channel and requires an acknowledgement
/// before a restart lease transition is considered committed.
/// </summary>
public sealed class WindowsSupervisionConnection : IRestartContinuityPort, ILockInputPort, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private int _disposed;

    private WindowsSupervisionConnection(
        NamedPipeClientStream pipe,
        StreamReader reader,
        StreamWriter writer,
        SupervisionResponse registration)
    {
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
        Registration = registration;
    }

    /// <summary>The Service response that established this desktop generation.</summary>
    public SupervisionResponse Registration { get; }

    /// <summary>Restricts Task Manager for the authenticated desktop user.</summary>
    public Task EnableAsync(CancellationToken cancellationToken) =>
        ExchangeTaskManagerAsync(
            SupervisionRequestKind.RestrictTaskManager,
            SupervisionResponseKind.TaskManagerRestricted,
            cancellationToken);

    /// <summary>Restores only the Task Manager policy changed by this installation.</summary>
    public Task DisableAsync(CancellationToken cancellationToken) =>
        ExchangeTaskManagerAsync(
            SupervisionRequestKind.RestoreTaskManager,
            SupervisionResponseKind.TaskManagerRestored,
            cancellationToken);

    private Task ExchangeTaskManagerAsync(
        SupervisionRequestKind requestKind,
        SupervisionResponseKind responseKind,
        CancellationToken cancellationToken) =>
        ExchangeForAcknowledgementAsync(
            new SupervisionRequest(
                SupervisionProtocol.CurrentVersion,
                requestKind,
                Process: null,
                Lease: null,
                Revision: null,
                LaunchToken: null),
            responseKind,
            expectedRevision: 0,
            cancellationToken);

    /// <summary>
    /// Connects to the machine-local ZARA Service, authenticates its SCM process before sending any
    /// registration data, and then proves the current desktop process identity.
    /// </summary>
    /// <param name="launchToken">
    /// The one-time token supplied by the Service, or null for a user-started desktop.
    /// </param>
    /// <param name="initialLease">
    /// The revision-zero restart instruction derived from the stored normal-time setting.
    /// </param>
    /// <param name="cancellationToken">Cancels the bounded registration handshake.</param>
    /// <returns>An acknowledged supervision connection.</returns>
    public static Task<WindowsSupervisionConnection> ConnectAsync(
        string? launchToken,
        SupervisionLease initialLease,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(
            SupervisionProtocol.PipeName,
            launchToken,
            initialLease,
            WindowsSupervisionServerVerifier.Default,
            cancellationToken);

    internal static async Task<WindowsSupervisionConnection> ConnectAsync(
        string pipeName,
        string? launchToken,
        SupervisionLease initialLease,
        ISupervisionServerVerifier serverVerifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(initialLease);
        ArgumentNullException.ThrowIfNull(serverVerifier);
        if (initialLease.Revision != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialLease),
                initialLease.Revision,
                "The initial supervision lease must have revision zero.");
        }

        using var connectCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCancellation.CancelAfter(ConnectTimeout);

        var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        StreamReader? reader = null;
        StreamWriter? writer = null;
        try
        {
            await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
            serverVerifier.Verify(pipe.SafePipeHandle);
            reader = new StreamReader(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            SupervisionRequest request = CreateRegistrationRequest(launchToken, initialLease);
            await WriteMessageAsync(writer, request, connectCancellation.Token).ConfigureAwait(false);
            SupervisionResponse response = await ReadMessageAsync<SupervisionResponse>(
                    reader,
                    connectCancellation.Token)
                .ConfigureAwait(false);
            if (response.ProtocolVersion != SupervisionProtocol.CurrentVersion ||
                response.Kind != SupervisionResponseKind.Registered ||
                response.AcknowledgedRevision < 0 ||
                (launchToken is null &&
                    response.AcknowledgedRevision != initialLease.Revision) ||
                response.ErrorCode is not null)
            {
                throw new InvalidOperationException(
                    $"The ZARA Service rejected desktop registration ({response.ErrorCode ?? "UNKNOWN"}).");
            }

            return new WindowsSupervisionConnection(pipe, reader, writer, response);
        }
        catch
        {
            await DisposeResourcesAsync(pipe, reader, writer).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<RestartContinuityAcknowledgement> PublishAsync(
        RestartContinuityLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lease),
                lease.Revision,
                "A published supervision lease must have a positive revision.");
        }

        var supervisionLease = new SupervisionLease(
            lease.Revision,
            lease.Decision.RestartRequired,
            lease.Decision.RecoverLock);
        await PublishLeaseAsync(supervisionLease, cancellationToken).ConfigureAwait(false);

        return new RestartContinuityAcknowledgement(lease.Revision);
    }

    /// <summary>
    /// Confirms that the desktop completed startup and, when required, projected every lock overlay.
    /// </summary>
    public Task ReportHealthyAsync(
        long revision,
        CancellationToken cancellationToken = default)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "A healthy supervision revision cannot be negative.");
        }

        return ExchangeForAcknowledgementAsync(
            new SupervisionRequest(
                SupervisionProtocol.CurrentVersion,
                SupervisionRequestKind.ReportHealthy,
                Process: null,
                Lease: null,
                Revision: revision,
                LaunchToken: null),
            SupervisionResponseKind.HealthyAcknowledged,
            revision,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RestartContinuityAcknowledgement> ReleaseAsync(
        RestartContinuityRelease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.Revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(release),
                release.Revision,
                "An explicit-exit supervision lease must have a positive revision.");
        }

        var releasedLease = new SupervisionLease(
            release.Revision,
            RestartRequiredAfterExit: false,
            RecoverLockOnRestart: false);
        await ExchangeForAcknowledgementAsync(
            new SupervisionRequest(
                SupervisionProtocol.CurrentVersion,
                SupervisionRequestKind.ReleaseForExplicitExit,
                Process: null,
                releasedLease,
                Revision: null,
                LaunchToken: null),
            SupervisionResponseKind.ExitAcknowledged,
            release.Revision,
            cancellationToken).ConfigureAwait(false);

        return new RestartContinuityAcknowledgement(release.Revision);
    }

    /// <summary>Closes the local channel without changing the last acknowledged Service lease.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _exchangeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeResourcesAsync(_pipe, _reader, _writer).ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    private static SupervisionRequest CreateRegistrationRequest(
        string? launchToken,
        SupervisionLease initialLease)
    {
        using Process process = Process.GetCurrentProcess();
        using WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent();
        string userSid = windowsIdentity.User?.Value ??
            throw new InvalidOperationException("Windows did not provide the desktop user SID.");
        var identity = new SupervisedProcessIdentity(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            process.SessionId,
            userSid);
        return new SupervisionRequest(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.Register,
            identity,
            initialLease,
            Revision: null,
            launchToken);
    }

    private async Task ExchangeForAcknowledgementAsync(
        SupervisionRequest request,
        SupervisionResponseKind expectedKind,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await WriteMessageAsync(_writer, request, cancellationToken).ConfigureAwait(false);
            SupervisionResponse response = await ReadMessageAsync<SupervisionResponse>(
                    _reader,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.ProtocolVersion != SupervisionProtocol.CurrentVersion ||
                response.Kind != expectedKind ||
                response.AcknowledgedRevision != expectedRevision ||
                response.RecoverLockOnStart ||
                response.ErrorCode is not null)
            {
                throw new InvalidOperationException(
                    $"The ZARA Service did not acknowledge revision {expectedRevision} " +
                    $"({response.ErrorCode ?? "UNEXPECTED_RESPONSE"}).");
            }
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    private async Task PublishLeaseAsync(
        SupervisionLease lease,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await WriteMessageAsync(
                    _writer,
                    new SupervisionRequest(
                        SupervisionProtocol.CurrentVersion,
                        SupervisionRequestKind.UpdateLease,
                        Process: null,
                        lease,
                        Revision: null,
                        LaunchToken: null),
                    cancellationToken)
                .ConfigureAwait(false);
            SupervisionResponse response = await ReadMessageAsync<SupervisionResponse>(
                    _reader,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.ProtocolVersion != SupervisionProtocol.CurrentVersion ||
                response.AcknowledgedRevision != lease.Revision ||
                response.RecoverLockOnStart ||
                response.ErrorCode is not null ||
                response.Kind is not (SupervisionResponseKind.LeaseAcknowledged or
                    SupervisionResponseKind.LeasePrepared))
            {
                throw new InvalidOperationException(
                    $"The ZARA Service did not acknowledge revision {lease.Revision} " +
                    $"({response.ErrorCode ?? "UNEXPECTED_RESPONSE"}).");
            }

            if (response.Kind == SupervisionResponseKind.LeasePrepared)
            {
                await WriteMessageAsync(
                        _writer,
                        new SupervisionRequest(
                            SupervisionProtocol.CurrentVersion,
                            SupervisionRequestKind.CommitLease,
                            Process: null,
                            Lease: null,
                            Revision: lease.Revision,
                            LaunchToken: null),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    private static async ValueTask DisposeResourcesAsync(
        NamedPipeClientStream pipe,
        StreamReader? reader,
        StreamWriter? writer)
    {
        if (writer is not null)
        {
            try
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The peer may have already closed a broken supervision channel.
            }
            catch (ObjectDisposedException)
            {
                // Cleanup is idempotent across a failed handshake and normal disposal.
            }
        }

        if (reader is not null)
        {
            try
            {
                reader.Dispose();
            }
            catch (IOException)
            {
                // The pipe still has to be released after a reader cleanup failure.
            }
            catch (ObjectDisposedException)
            {
                // Cleanup is idempotent across a failed handshake and normal disposal.
            }
        }

        try
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A broken pipe must not replace the operation result during async cleanup.
        }
        catch (ObjectDisposedException)
        {
            // Cleanup is idempotent across a failed handshake and normal disposal.
        }
    }

    internal static async Task WriteMessageAsync<T>(
        StreamWriter writer,
        T message,
        CancellationToken cancellationToken)
    {
        string serialized = JsonSerializer.Serialize(message, JsonOptions);
        if (serialized.Length > SupervisionProtocol.MaximumMessageCharacters)
        {
            throw new InvalidOperationException("The supervision message exceeded its size limit.");
        }

        await writer.WriteLineAsync(serialized.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T> ReadMessageAsync<T>(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(capacity: 256);
        var buffer = new char[1];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The supervision channel closed before a response.");
            }

            if (buffer[0] == '\n')
            {
                break;
            }

            if (builder.Length == SupervisionProtocol.MaximumMessageCharacters + 1)
            {
                throw new InvalidDataException("The supervision response exceeded its size limit.");
            }

            builder.Append(buffer[0]);
            if (builder.Length > SupervisionProtocol.MaximumMessageCharacters &&
                buffer[0] != '\r')
            {
                throw new InvalidDataException("The supervision response exceeded its size limit.");
            }
        }

        if (builder.Length > 0 && builder[^1] == '\r')
        {
            builder.Length--;
        }

        return JsonSerializer.Deserialize<T>(builder.ToString(), JsonOptions) ??
            throw new InvalidDataException("The supervision response was empty or invalid.");
    }
}
