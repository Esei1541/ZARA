using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Elects one Desktop process per Windows user session and carries later Explorer launch requests
/// to its settings window without changing the Service supervision protocol.
/// </summary>
public sealed partial class WindowsDesktopActivationChannel : IDisposable
{
    private const string PipeNamePrefix = "ZARA.Desktop.Activation";
    private const string ActivateRequest = "ACTIVATE/1";
    private const string ActivateAcknowledgement = "ACTIVATED/1";
    private const int MaximumMessageCharacters = 64;
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ElectionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(5);

    private readonly NamedPipeServerStream _server;
    private readonly string _executablePath;
    private readonly int _sessionId;
    private readonly CancellationTokenSource _stopping = new();
    private Func<Task>? _activationRequested;
    private Task? _listener;
    private int _disposeState;

    private WindowsDesktopActivationChannel(
        NamedPipeServerStream server,
        string executablePath,
        int sessionId)
    {
        _server = server;
        _executablePath = executablePath;
        _sessionId = sessionId;
    }

    /// <summary>
    /// For a normal Explorer launch, activates an existing Desktop or elects this process as the
    /// session's primary instance. A null result means the existing window acknowledged the request.
    /// </summary>
    public static async Task<WindowsDesktopActivationChannel?> AcquireOrActivateAsync(
        CancellationToken cancellationToken = default)
    {
        ActivationIdentity identity = ReadCurrentIdentity();
        if (await TryActivateExistingAsync(identity, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return CreatePrimary(identity);
        }
        catch (IOException)
        {
            using var election = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            election.CancelAfter(ElectionTimeout);
            while (true)
            {
                election.Token.ThrowIfCancellationRequested();
                if (await TryActivateExistingAsync(identity, election.Token).ConfigureAwait(false))
                {
                    return null;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), election.Token)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Acquires the primary endpoint for a Service-authenticated launch. This path never turns a
    /// Service recovery request into a window-only activation.
    /// </summary>
    public static WindowsDesktopActivationChannel AcquirePrimary()
    {
        return CreatePrimary(ReadCurrentIdentity());
    }

    /// <summary>
    /// Starts accepting bounded activation requests from the exact same executable in this user
    /// session. The acknowledgement is sent only after the supplied callback completes.
    /// </summary>
    public void StartListening(Func<Task> activationRequested)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        ArgumentNullException.ThrowIfNull(activationRequested);
        if (Interlocked.CompareExchange(ref _activationRequested, activationRequested, null) is not null)
        {
            throw new InvalidOperationException(
                "The Desktop activation listener has already started.");
        }

        _listener = ListenAsync(_stopping.Token);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        _server.Dispose();
        Task? listener = _listener;
        if (listener is not null)
        {
#pragma warning disable CA1031 // Cancellation and pipe disposal are expected during process exit.
            try
            {
                listener.GetAwaiter().GetResult();
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
            }
#pragma warning restore CA1031
        }

        _stopping.Dispose();
    }

    private static WindowsDesktopActivationChannel CreatePrimary(ActivationIdentity identity)
    {
        var server = new NamedPipeServerStream(
            identity.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
                PipeOptions.CurrentUserOnly |
                PipeOptions.FirstPipeInstance,
            inBufferSize: 512,
            outBufferSize: 512);
        return new WindowsDesktopActivationChannel(
            server,
            identity.ExecutablePath,
            identity.SessionId);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var messageCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                messageCancellation.CancelAfter(MessageTimeout);
                AuthenticatePeer(
                    _server.SafePipeHandle,
                    readServerProcessId: false,
                    _executablePath,
                    _sessionId);
                using var reader = new StreamReader(
                    _server,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 128,
                    leaveOpen: true);
                await using var writer = new StreamWriter(
                    _server,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 128,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                };
                string request = await ReadBoundedLineAsync(reader, messageCancellation.Token)
                    .ConfigureAwait(false);
                if (!string.Equals(request, ActivateRequest, StringComparison.Ordinal))
                {
                    continue;
                }

                Func<Task> callback = _activationRequested ??
                    throw new InvalidOperationException(
                        "The Desktop activation callback is not initialized.");
                await callback().WaitAsync(messageCancellation.Token).ConfigureAwait(false);
                await writer.WriteLineAsync(
                        ActivateAcknowledgement.AsMemory(),
                        messageCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace.TraceError("A Desktop activation request was rejected: {0}", exception);
            }
            finally
            {
                if (_server.IsConnected)
                {
                    _server.Disconnect();
                }
            }
        }
    }

    /// <summary>Requests activation without claiming the primary endpoint.</summary>
    public static Task<bool> TryActivateExistingAsync(CancellationToken cancellationToken = default) =>
        TryActivateExistingAsync(ReadCurrentIdentity(), cancellationToken);

    private static async Task<bool> TryActivateExistingAsync(
        ActivationIdentity identity,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            identity.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous |
                PipeOptions.CurrentUserOnly,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(ConnectAttemptTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        AuthenticatePeer(
            pipe.SafePipeHandle,
            readServerProcessId: true,
            identity.ExecutablePath,
            identity.SessionId);
        using var messageCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        messageCancellation.CancelAfter(MessageTimeout);
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 128,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 128,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        await writer.WriteLineAsync(ActivateRequest.AsMemory(), messageCancellation.Token)
            .ConfigureAwait(false);
        string acknowledgement = await ReadBoundedLineAsync(
                reader,
                messageCancellation.Token)
            .ConfigureAwait(false);
        return string.Equals(
            acknowledgement,
            ActivateAcknowledgement,
            StringComparison.Ordinal);
    }

    private static void AuthenticatePeer(
        SafePipeHandle pipeHandle,
        bool readServerProcessId,
        string expectedExecutablePath,
        int expectedSessionId)
    {
        bool read = readServerProcessId
            ? GetNamedPipeServerProcessId(pipeHandle, out uint processIdValue)
            : GetNamedPipeClientProcessId(pipeHandle, out processIdValue);
        if (!read || processIdValue == 0 || processIdValue > int.MaxValue)
        {
            throw new InvalidDataException(
                "The Desktop activation peer process could not be identified.");
        }

        using Process process = Process.GetProcessById((int)processIdValue);
        if (process.HasExited || process.SessionId != expectedSessionId)
        {
            throw new InvalidDataException(
                "The Desktop activation peer session did not match.");
        }

        string actualPath = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
        if (!string.Equals(
                actualPath,
                expectedExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The Desktop activation peer executable did not match.");
        }

        if (readServerProcessId)
        {
            _ = AllowSetForegroundWindow((int)processIdValue);
        }
    }

    private static async Task<string> ReadBoundedLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(capacity: 32);
        var buffer = new char[1];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The Desktop activation channel closed before a complete message.");
            }

            if (buffer[0] == '\n')
            {
                return builder.ToString();
            }

            if (buffer[0] != '\r')
            {
                builder.Append(buffer[0]);
                if (builder.Length > MaximumMessageCharacters)
                {
                    throw new InvalidDataException(
                        "The Desktop activation message exceeded its size limit.");
                }
            }
        }
    }

    private static ActivationIdentity ReadCurrentIdentity()
    {
        string executablePath = Path.GetFullPath(
            Environment.ProcessPath ??
            throw new InvalidOperationException(
                "The current Desktop executable path could not be read."));
        int sessionId = Process.GetCurrentProcess().SessionId;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        string userSid = identity.User?.Value ??
            throw new InvalidOperationException(
                "The current process token did not contain a Windows user SID.");
        string key = $"{userSid}|{sessionId}|{executablePath.ToUpperInvariant()}";
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return new ActivationIdentity(
            $"{PipeNamePrefix}.{digest}",
            executablePath,
            sessionId);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    private readonly record struct ActivationIdentity(
        string PipeName,
        string ExecutablePath,
        int SessionId);
}
