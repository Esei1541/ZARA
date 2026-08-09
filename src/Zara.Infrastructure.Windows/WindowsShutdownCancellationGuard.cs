using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Zara.Application.SystemPower;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Arms a same-user companion process before Windows shutdown so a later cancellation can restore
/// the ZARA lock even when WPF has already terminated the desktop process.
/// </summary>
/// <remarks>
/// The companion is the exact running desktop executable in a non-WPF worker mode. A random,
/// current-user-only named pipe binds it to this process instance before any lock cleanup begins.
/// Disposing an armed instance closes the parent channel but intentionally does not terminate the
/// companion: Windows may already be ending the WPF process, and the companion must remain alive
/// until it receives the final session result.
/// </remarks>
public sealed class WindowsShutdownCancellationGuard : IDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResultReceiptTimeout = TimeSpan.FromSeconds(5);
    private readonly Func<Task<ShutdownCancellationRecoveryResult>> _recoverCancellation;
    private readonly Func<bool, Task> _acknowledgementCompleted;
    private readonly Func<Exception, Task> _guardFaulted;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _workerProcess;
    private Task? _listenerTask;
    private int _disposed;

    private WindowsShutdownCancellationGuard(
        Func<Task<ShutdownCancellationRecoveryResult>> recoverCancellation,
        Func<bool, Task> acknowledgementCompleted,
        Func<Exception, Task> guardFaulted)
    {
        _recoverCancellation = recoverCancellation;
        _acknowledgementCompleted = acknowledgementCompleted;
        _guardFaulted = guardFaulted;
    }

    /// <summary>
    /// Starts the companion and waits until its native session-end window and shutdown priority are
    /// ready. Callers must complete this method before removing any ZARA lock effect.
    /// </summary>
    /// <param name="recoverCancellation">
    /// Restores or reconciles the preceding lock intent when Windows cancels shutdown.
    /// </param>
    /// <param name="restoreLockIfParentExits">
    /// Whether the companion may relaunch this exact executable to restore the lock after the
    /// original WPF process exits before cancellation is reported.
    /// </param>
    /// <param name="acknowledgementCompleted">
    /// Releases the caller's lifecycle gate only after the recovery result has been written to the
    /// companion. This prevents a later exit or shutdown request from closing the channel between
    /// recovery and its acknowledgement.
    /// </param>
    /// <param name="guardFaulted">
    /// Reconciles the lock and reports a visible failure when the companion channel ends without a
    /// valid cancellation exchange. The companion is terminated only after this callback succeeds.
    /// </param>
    /// <param name="cancellationToken">Cancels only the pre-shutdown arm handshake.</param>
    /// <returns>An armed guard owned by the caller.</returns>
    public static async Task<WindowsShutdownCancellationGuard> ArmAsync(
        Func<Task<ShutdownCancellationRecoveryResult>> recoverCancellation,
        bool restoreLockIfParentExits,
        Func<bool, Task> acknowledgementCompleted,
        Func<Exception, Task> guardFaulted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recoverCancellation);
        ArgumentNullException.ThrowIfNull(acknowledgementCompleted);
        ArgumentNullException.ThrowIfNull(guardFaulted);

        var guard = new WindowsShutdownCancellationGuard(
            recoverCancellation,
            acknowledgementCompleted,
            guardFaulted);
        try
        {
            await guard
                .ArmCoreAsync(restoreLockIfParentExits, cancellationToken)
                .ConfigureAwait(false);
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Prevents the companion from relaunching a stale lock after a newer safety-unlock intent.
    /// </summary>
    public Task SuppressFallbackRecoveryAsync(CancellationToken cancellationToken = default) =>
        WriteMessageAsync(ShutdownGuardProtocol.SuppressFallback, cancellationToken);

    /// <summary>
    /// Tells an armed companion that the platform shutdown request failed before acceptance.
    /// </summary>
    /// <param name="cancellationToken">Cancels the best-effort disarm notification.</param>
    public async Task DisarmAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

#pragma warning disable CA1031 // A broken worker channel is equivalent to an already-disarmed guard.
        try
        {
            await WriteMessageAsync(
                    ShutdownGuardProtocol.Disarm,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031

        Dispose();
    }

    /// <summary>
    /// Releases local IPC handles without killing a companion that may be observing session end.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        _pipe?.Dispose();
        _pipe = null;
        _reader?.Dispose();
        _reader = null;
        _writer?.Dispose();
        _writer = null;
        _workerProcess?.Dispose();
        _workerProcess = null;
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ArmCoreAsync(
        bool restoreLockIfParentExits,
        CancellationToken cancellationToken)
    {
        string executablePath = Environment.ProcessPath ??
            throw new InvalidOperationException("Windows did not provide the current executable path.");
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new InvalidOperationException("The current executable path is not absolute.");
        }

        using Process parentProcess = Process.GetCurrentProcess();
        string pipeName = $"zara-shutdown-{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(ShutdownGuardProtocol.WorkerSwitch);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(parentProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(
            parentProcess.StartTime.ToUniversalTime().Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        using var handshakeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCancellation.CancelAfter(HandshakeTimeout);

        Task connection = _pipe.WaitForConnectionAsync(handshakeCancellation.Token);
        _workerProcess = Process.Start(startInfo) ??
            throw new InvalidOperationException("Windows did not start the shutdown guard process.");
        await connection.ConfigureAwait(false);

        _reader = new StreamReader(
            _pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        _writer = new StreamWriter(
            _pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };

        string? ready = await _reader
            .ReadLineAsync(handshakeCancellation.Token)
            .ConfigureAwait(false);
        if (!string.Equals(ready, ShutdownGuardProtocol.Ready, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The shutdown guard did not complete its ready handshake.");
        }

        await WriteMessageAsync(
                restoreLockIfParentExits
                    ? ShutdownGuardProtocol.ArmWithFallback
                    : ShutdownGuardProtocol.ArmWithoutFallback,
                handshakeCancellation.Token)
            .ConfigureAwait(false);
        _listenerTask = ListenForCancellationAsync(
            _reader,
            _lifetimeCancellation.Token);
    }

    private async Task ListenForCancellationAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA1031 // The worker disappearing is expected during successful Windows shutdown.
        try
        {
            string? message = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(
                    message,
                    ShutdownGuardProtocol.SessionEnding,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (!string.Equals(
                    message,
                    ShutdownGuardProtocol.Cancelled,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The shutdown guard channel ended without a cancellation result.");
            }

            string acknowledgement;
            bool recoveryFailed = false;
            try
            {
                ShutdownCancellationRecoveryResult recovery =
                    await _recoverCancellation().ConfigureAwait(false);
                acknowledgement = recovery switch
                {
                    ShutdownCancellationRecoveryResult.LockRestored =>
                        ShutdownGuardProtocol.Recovered,
                    ShutdownCancellationRecoveryResult.LockNotRequired =>
                        ShutdownGuardProtocol.NotRequired,
                    ShutdownCancellationRecoveryResult.SupersededByNewerIntent =>
                        ShutdownGuardProtocol.Superseded,
                    _ => ShutdownGuardProtocol.NotRequired,
                };
            }
            catch (Exception)
            {
                acknowledgement = ShutdownGuardProtocol.RecoveryFailed;
                recoveryFailed = true;
            }

            await WriteMessageAsync(acknowledgement, CancellationToken.None)
                .ConfigureAwait(false);
            string? receipt = await reader
                .ReadLineAsync(CancellationToken.None)
                .AsTask()
                .WaitAsync(ResultReceiptTimeout, CancellationToken.None)
                .ConfigureAwait(false);
            if (!string.Equals(
                    receipt,
                    ShutdownGuardProtocol.ResultReceived,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The shutdown guard did not confirm the recovery result.");
            }

            await _acknowledgementCompleted(recoveryFailed).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await HandleGuardFaultAsync(exception).ConfigureAwait(false);
        }
#pragma warning restore CA1031
    }

    private async Task HandleGuardFaultAsync(Exception exception)
    {
#pragma warning disable CA1031 // A failed parent recovery must leave the companion fallback intact.
        try
        {
            await _guardFaulted(exception).ConfigureAwait(false);
            StopWorkerAfterParentRecovery();
            await _acknowledgementCompleted(false).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private void StopWorkerAfterParentRecovery()
    {
        Process? worker = _workerProcess;
        if (worker is null)
        {
            return;
        }

        try
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: false);
            }
        }
        catch (InvalidOperationException) when (worker.HasExited)
        {
        }

        if (!worker.WaitForExit(milliseconds: 5000))
        {
            throw new InvalidOperationException(
                "The shutdown guard process did not stop after parent recovery.");
        }
    }

    private async Task WriteMessageAsync(string message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            StreamWriter writer = _writer ??
                throw new InvalidOperationException("The shutdown guard is not connected.");
            await writer.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

internal static class ShutdownGuardProtocol
{
    internal const string WorkerSwitch = "--zara-shutdown-guard";
    internal const string RecoverySwitch = "--zara-restore-lock-after-shutdown-cancel";
    internal const string Ready = "READY";
    internal const string ArmWithFallback = "ARM_LOCK";
    internal const string ArmWithoutFallback = "ARM_NO_LOCK";
    internal const string Disarm = "DISARM";
    internal const string SuppressFallback = "SUPPRESS_FALLBACK";
    internal const string Cancelled = "CANCELLED";
    internal const string SessionEnding = "SESSION_ENDING";
    internal const string Recovered = "RECOVERED";
    internal const string NotRequired = "NOT_REQUIRED";
    internal const string Superseded = "SUPERSEDED";
    internal const string RecoveryFailed = "RECOVERY_FAILED";
    internal const string ResultReceived = "RESULT_RECEIVED";
    internal const string Restore = "RESTORE";
}
