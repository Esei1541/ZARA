using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Selects the non-WPF shutdown-guard process mode and recognizes the one-time recovery launch.
/// </summary>
public static partial class WindowsShutdownGuardProcess
{
    private const uint ApplicationFirstShutdownLevel = 0x3FF;
    private static readonly TimeSpan ParentExitQueryGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ParentRecoveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RelaunchRecoveryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RelaunchRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FailedRecoveryCleanupTimeout = TimeSpan.FromSeconds(5);
    private const int RelaunchAttemptCount = 3;

    /// <summary>
    /// Runs shutdown-guard worker mode when the exact internal switch is present.
    /// </summary>
    /// <param name="arguments">The process arguments excluding the executable path.</param>
    /// <param name="exitCode">The worker exit code when worker mode was selected.</param>
    /// <returns><see langword="true"/> when worker mode consumed the process.</returns>
    public static bool TryRunWorker(string[] arguments, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0 ||
            !string.Equals(
                arguments[0],
                ShutdownGuardProtocol.WorkerSwitch,
                StringComparison.Ordinal))
        {
            exitCode = 0;
            return false;
        }

        exitCode = RunWorker(arguments);
        return true;
    }

    /// <summary>
    /// Returns whether the desktop process was relaunched to restore a lock after canceled shutdown.
    /// </summary>
    /// <param name="arguments">The WPF startup arguments.</param>
    public static bool IsRecoveryLaunch(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Count == 5 &&
            string.Equals(
                arguments[0],
                ShutdownGuardProtocol.RecoverySwitch,
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates a one-time worker handshake, restores the lock, and acknowledges actual completion.
    /// </summary>
    /// <param name="arguments">The exact recovery arguments issued by the guard worker.</param>
    /// <param name="restoreLock">Restores all lock overlays before acknowledgement.</param>
    /// <param name="cancellationToken">Cancels the bounded recovery handshake.</param>
    public static async Task CompleteRecoveryLaunchAsync(
        IReadOnlyList<string> arguments,
        Func<Task> restoreLock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(restoreLock);
        RecoveryLaunchArguments recovery = ParseRecoveryLaunch(arguments);
        ValidateExactWorkerProcess(recovery.WorkerId, recovery.WorkerStartTimeUtcTicks);

        using var handshakeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCancellation.CancelAfter(RelaunchRecoveryTimeout);
        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            recovery.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(handshakeCancellation.Token).ConfigureAwait(false);
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };

        await writer.WriteLineAsync(recovery.Token).ConfigureAwait(false);
        string? command = await reader
            .ReadLineAsync(handshakeCancellation.Token)
            .ConfigureAwait(false);
        if (!string.Equals(command, ShutdownGuardProtocol.Restore, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The shutdown guard did not authorize lock recovery.");
        }

        try
        {
            await restoreLock().ConfigureAwait(false);
            await writer.WriteLineAsync(ShutdownGuardProtocol.Recovered).ConfigureAwait(false);
        }
        catch
        {
#pragma warning disable CA1031 // Best-effort failure acknowledgement must preserve the restore exception.
            try
            {
                await writer
                    .WriteLineAsync(ShutdownGuardProtocol.RecoveryFailed)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
#pragma warning restore CA1031
            throw;
        }
    }

    private static int RunWorker(string[] arguments)
    {
        if (arguments.Length != 4 ||
            string.IsNullOrWhiteSpace(arguments[1]) ||
            !int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out int parentId) ||
            !long.TryParse(
                arguments[3],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parentStartTimeUtcTicks))
        {
            return 2;
        }

#pragma warning disable CA1031 // Worker failures must return a code without opening WPF error UI.
        try
        {
            using Process parentProcess = GetExactParentProcess(parentId, parentStartTimeUtcTicks);
            ConfigureFirstApplicationShutdownNotification();
            return RunMessageLoop(arguments[1], parentProcess);
        }
        catch (Exception exception)
        {
            TryWriteWorkerFailure(exception);
            return 3;
        }
#pragma warning restore CA1031
    }

    private static void TryWriteWorkerFailure(Exception exception)
    {
#pragma warning disable CA1031 // Worker diagnostics must never replace the stable exit code.
        try
        {
            Console.Error.WriteLine(exception);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private static int RunMessageLoop(string pipeName, Process parentProcess)
    {
        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        pipe.Connect(5000);

        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };
        try
        {
            using var window = new ShutdownGuardWindow();

            writer.WriteLine(ShutdownGuardProtocol.Ready);
            string? arm = reader.ReadLine();
            bool fallbackRecoveryRequired = arm switch
            {
                ShutdownGuardProtocol.ArmWithFallback => true,
                ShutdownGuardProtocol.ArmWithoutFallback => false,
                _ => throw new InvalidOperationException(
                    "The shutdown guard received an invalid arm policy."),
            };
            if (arm is null)
            {
                return 4;
            }

            Task<int> protocolTask = ObserveSessionResultAsync(
                reader,
                writer,
                window,
                parentProcess,
                fallbackRecoveryRequired);
            _ = protocolTask.ContinueWith(
                static (_, state) => ((ShutdownGuardWindow)state!).RequestMessageLoopExit(),
                window,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            System.Windows.Forms.Application.Run();
            return protocolTask.GetAwaiter().GetResult();
        }
        finally
        {
            DisposeParentWriter(writer);
        }
    }

    private static void DisposeParentWriter(StreamWriter writer)
    {
        try
        {
            writer.Dispose();
        }
        catch (IOException)
        {
            // The parent channel is expected to be broken when WPF exits during shutdown.
        }
    }

    private static async Task<int> ObserveSessionResultAsync(
        StreamReader reader,
        StreamWriter writer,
        ShutdownGuardWindow window,
        Process parentProcess,
        bool fallbackRecoveryRequired)
    {
        Task<ParentMessageRead> parentMessage = ReadParentMessageAsync(reader);
        Task<bool> sessionEnd = window.SessionEnd;
        while (true)
        {
            Task completed = await Task.WhenAny(parentMessage, sessionEnd).ConfigureAwait(false);

            if (completed == parentMessage)
            {
                ParentMessageRead parentRead = await parentMessage.ConfigureAwait(false);
                if (!parentRead.IsAvailable)
                {
                    return await RecoverAfterParentBecameUnavailableAsync(
                            window,
                            parentProcess,
                            fallbackRecoveryRequired)
                        .ConfigureAwait(false);
                }

                string message = parentRead.Message;
                if (string.Equals(
                        message,
                        ShutdownGuardProtocol.SuppressFallback,
                        StringComparison.Ordinal))
                {
                    fallbackRecoveryRequired = false;
                    parentMessage = ReadParentMessageAsync(reader);
                    continue;
                }

                if (string.Equals(message, ShutdownGuardProtocol.Disarm, StringComparison.Ordinal))
                {
                    return 0;
                }

                return 5;
            }

            bool sessionIsEnding = await sessionEnd.ConfigureAwait(false);
            if (sessionIsEnding)
            {
                await TryNotifyParentSessionIsEndingAsync(writer).ConfigureAwait(false);
                return 0;
            }

            ParentRecoveryAttempt parentRecovery = await TryRecoverThroughParentAsync(
                    parentMessage,
                    reader,
                    writer,
                    fallbackRecoveryRequired)
                .ConfigureAwait(false);
            fallbackRecoveryRequired = parentRecovery.FallbackRecoveryRequired;
            if (parentRecovery.Outcome == ParentRecoveryOutcome.Recovered)
            {
                return 0;
            }

            if (!fallbackRecoveryRequired)
            {
                return 7;
            }

            if (parentRecovery.Outcome == ParentRecoveryOutcome.Failed)
            {
                ParentMessageRead parentDecision = await ReadParentMessageAsync(reader)
                    .ConfigureAwait(false);
                if (string.Equals(
                        parentDecision.Message,
                        ShutdownGuardProtocol.SuppressFallback,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        parentDecision.Message,
                        ShutdownGuardProtocol.Disarm,
                        StringComparison.Ordinal))
                {
                    return 7;
                }

                if (parentDecision.IsAvailable)
                {
                    return 5;
                }
            }

            if (!await WaitForExactParentExitAsync(parentProcess).ConfigureAwait(false))
            {
                return 8;
            }

            return await LaunchRecoveryProcessAsync().ConfigureAwait(false) ? 0 : 6;
        }
    }

    private static async Task<int> RecoverAfterParentBecameUnavailableAsync(
        ShutdownGuardWindow window,
        Process parentProcess,
        bool fallbackRecoveryRequired)
    {
        bool? endResult = await WaitForSessionResultAfterParentExitAsync(window)
            .ConfigureAwait(false);
        if (endResult is true)
        {
            return 0;
        }

        if (!fallbackRecoveryRequired)
        {
            return 0;
        }

        if (!await WaitForExactParentExitAsync(parentProcess).ConfigureAwait(false))
        {
            return 8;
        }

        return await LaunchRecoveryProcessAsync().ConfigureAwait(false) ? 0 : 6;
    }

    private static async Task<ParentMessageRead> ReadParentMessageAsync(StreamReader reader)
    {
        try
        {
            string? message = await reader.ReadLineAsync().ConfigureAwait(false);
            return message is null
                ? ParentMessageRead.Unavailable
                : new ParentMessageRead(message, IsAvailable: true);
        }
        catch (IOException)
        {
            return ParentMessageRead.Unavailable;
        }
        catch (ObjectDisposedException)
        {
            return ParentMessageRead.Unavailable;
        }
    }

    private static async Task TryNotifyParentSessionIsEndingAsync(StreamWriter writer)
    {
#pragma warning disable CA1031 // The parent may already be gone during successful Windows shutdown.
        try
        {
            await writer
                .WriteLineAsync(ShutdownGuardProtocol.SessionEnding)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private static async Task<bool?> WaitForSessionResultAfterParentExitAsync(
        ShutdownGuardWindow window)
    {
        if (window.QueryReceived)
        {
            return await window.SessionEnd.ConfigureAwait(false);
        }

        try
        {
            return await window.SessionEnd
                .WaitAsync(ParentExitQueryGrace)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<ParentRecoveryAttempt> TryRecoverThroughParentAsync(
        Task<ParentMessageRead> pendingParentMessage,
        StreamReader reader,
        StreamWriter writer,
        bool fallbackRecoveryRequired)
    {
#pragma warning disable CA1031 // A broken parent channel falls back to exact executable recovery.
        try
        {
            await writer.WriteLineAsync(ShutdownGuardProtocol.Cancelled).ConfigureAwait(false);
            Task<ParentMessageRead> nextMessage = pendingParentMessage;
            while (true)
            {
                ParentMessageRead parentRead = await nextMessage
                    .WaitAsync(ParentRecoveryTimeout)
                    .ConfigureAwait(false);
                if (!parentRead.IsAvailable)
                {
                    return new ParentRecoveryAttempt(
                        ParentRecoveryOutcome.Unavailable,
                        fallbackRecoveryRequired);
                }

                string acknowledgement = parentRead.Message;
                if (string.Equals(
                        acknowledgement,
                        ShutdownGuardProtocol.SuppressFallback,
                        StringComparison.Ordinal))
                {
                    fallbackRecoveryRequired = false;
                    nextMessage = ReadParentMessageAsync(reader);
                    continue;
                }

                if (string.Equals(
                        acknowledgement,
                        ShutdownGuardProtocol.Recovered,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        acknowledgement,
                        ShutdownGuardProtocol.NotRequired,
                        StringComparison.Ordinal))
                {
                    await writer
                        .WriteLineAsync(ShutdownGuardProtocol.ResultReceived)
                        .ConfigureAwait(false);
                    return new ParentRecoveryAttempt(
                        ParentRecoveryOutcome.Recovered,
                        fallbackRecoveryRequired);
                }

                if (string.Equals(
                        acknowledgement,
                        ShutdownGuardProtocol.Superseded,
                        StringComparison.Ordinal))
                {
                    await writer
                        .WriteLineAsync(ShutdownGuardProtocol.ResultReceived)
                        .ConfigureAwait(false);
                    return new ParentRecoveryAttempt(
                        ParentRecoveryOutcome.Recovered,
                        FallbackRecoveryRequired: false);
                }

                if (string.Equals(
                        acknowledgement,
                        ShutdownGuardProtocol.RecoveryFailed,
                        StringComparison.Ordinal))
                {
                    await writer
                        .WriteLineAsync(ShutdownGuardProtocol.ResultReceived)
                        .ConfigureAwait(false);
                    return new ParentRecoveryAttempt(
                        ParentRecoveryOutcome.Failed,
                        fallbackRecoveryRequired);
                }

                return new ParentRecoveryAttempt(
                    ParentRecoveryOutcome.Unavailable,
                    fallbackRecoveryRequired);
            }
        }
        catch (Exception)
        {
            return new ParentRecoveryAttempt(
                ParentRecoveryOutcome.Unavailable,
                fallbackRecoveryRequired);
        }
#pragma warning restore CA1031
    }

    private static async Task<bool> WaitForExactParentExitAsync(Process parentProcess)
    {
#pragma warning disable CA1031 // Failure to verify parent exit must prevent a duplicate recovery process.
        try
        {
            if (!parentProcess.HasExited)
            {
                await parentProcess.WaitForExitAsync().ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }

    private static async Task<bool> LaunchRecoveryProcessAsync()
    {
        string executablePath = Environment.ProcessPath ?? string.Empty;
        if (!Path.IsPathFullyQualified(executablePath))
        {
            return false;
        }

        return await ExecuteRecoveryLaunchRetryAsync(
                () => TryLaunchRecoveryProcessOnceAsync(executablePath),
                RelaunchAttemptCount,
                RelaunchRetryDelay,
                static delay => Task.Delay(delay))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Executes bounded recovery launches serially and retries only after the preceding attempt has
    /// confirmed that no failed child remains alive.
    /// </summary>
    /// <param name="launchAttempt">
    /// Runs one recovery launch and reports its cleanup-safe outcome. An exception is not considered
    /// retryable because it does not prove that a started child has been stopped.
    /// </param>
    /// <param name="attemptCount">The maximum number of launch attempts.</param>
    /// <param name="retryDelay">The delay inserted before each retry after the first attempt.</param>
    /// <param name="delayAsync">Waits for the configured retry delay.</param>
    /// <returns><see langword="true" /> only when one attempt acknowledges recovery.</returns>
    internal static async Task<bool> ExecuteRecoveryLaunchRetryAsync(
        Func<Task<RecoveryAttemptOutcome>> launchAttempt,
        int attemptCount,
        TimeSpan retryDelay,
        Func<TimeSpan, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(launchAttempt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptCount);
        if (retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryDelay),
                retryDelay,
                "The recovery retry delay cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(delayAsync);

        for (int attempt = 0; attempt < attemptCount; attempt++)
        {
            if (attempt > 0)
            {
                await delayAsync(retryDelay).ConfigureAwait(false);
            }

            RecoveryAttemptOutcome outcome = await launchAttempt().ConfigureAwait(false);

            if (outcome == RecoveryAttemptOutcome.Recovered)
            {
                return true;
            }

            if (outcome == RecoveryAttemptOutcome.FailedChildStillRunning)
            {
                return false;
            }
        }

        return false;
    }

    private static async Task<RecoveryAttemptOutcome> TryLaunchRecoveryProcessOnceAsync(
        string executablePath)
    {
        Process? recoveryProcess = null;
        bool recovered = false;
        RecoveryAttemptOutcome outcome = RecoveryAttemptOutcome.RetryableFailure;

#pragma warning disable CA1031 // Each bounded attempt is followed by exact child cleanup before retry.
        try
        {
            string pipeName = $"zara-recovery-{Guid.NewGuid():N}";
            string token = Guid.NewGuid().ToString("N");
            using Process workerProcess = Process.GetCurrentProcess();
            using var pipe = new NamedPipeServerStream(
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
            };
            startInfo.ArgumentList.Add(ShutdownGuardProtocol.RecoverySwitch);
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add(token);
            startInfo.ArgumentList.Add(workerProcess.Id.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(
                workerProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            recoveryProcess = Process.Start(startInfo);
            if (recoveryProcess is not null)
            {
                using var handshakeCancellation =
                    new CancellationTokenSource(RelaunchRecoveryTimeout);
                await pipe.WaitForConnectionAsync(handshakeCancellation.Token).ConfigureAwait(false);
                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);
                using var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                };
                string? suppliedToken = await reader
                    .ReadLineAsync(handshakeCancellation.Token)
                    .ConfigureAwait(false);
                if (string.Equals(suppliedToken, token, StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync(ShutdownGuardProtocol.Restore).ConfigureAwait(false);
                    string? recoveryResult = await reader
                        .ReadLineAsync(handshakeCancellation.Token)
                        .ConfigureAwait(false);
                    recovered = string.Equals(
                        recoveryResult,
                        ShutdownGuardProtocol.Recovered,
                        StringComparison.Ordinal);
                    if (recovered)
                    {
                        outcome = RecoveryAttemptOutcome.Recovered;
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        if (!recovered && recoveryProcess is not null)
        {
            bool stopped = await StopFailedRecoveryProcessAsync(recoveryProcess)
                .ConfigureAwait(false);
            if (!stopped)
            {
                outcome = RecoveryAttemptOutcome.FailedChildStillRunning;
            }
        }

        recoveryProcess?.Dispose();
        return outcome;
#pragma warning restore CA1031
    }

    /// <summary>
    /// Stops the exact recovery child returned by <see cref="Process.Start(ProcessStartInfo)" /> and
    /// verifies its exit before another recovery attempt can begin.
    /// </summary>
    /// <param name="recoveryProcess">The exact child process owned by the failed attempt.</param>
    /// <returns><see langword="true" /> only when the child is confirmed stopped.</returns>
    internal static async Task<bool> StopFailedRecoveryProcessAsync(Process recoveryProcess)
    {
#pragma warning disable CA1031 // Failure to verify exact cleanup must stop retries to avoid duplicates.
        try
        {
            if (recoveryProcess.HasExited)
            {
                return true;
            }

            recoveryProcess.Kill(entireProcessTree: false);
            await recoveryProcess
                .WaitForExitAsync()
                .WaitAsync(FailedRecoveryCleanupTimeout)
                .ConfigureAwait(false);
            return recoveryProcess.HasExited;
        }
        catch (Exception)
        {
            try
            {
                return recoveryProcess.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }
#pragma warning restore CA1031
    }

    private static RecoveryLaunchArguments ParseRecoveryLaunch(IReadOnlyList<string> arguments)
    {
        if (!IsRecoveryLaunch(arguments) ||
            string.IsNullOrWhiteSpace(arguments[1]) ||
            !Guid.TryParseExact(arguments[2], "N", out _) ||
            !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out int workerId) ||
            !long.TryParse(
                arguments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long workerStartTimeUtcTicks))
        {
            throw new ArgumentException("The shutdown recovery launch arguments are invalid.", nameof(arguments));
        }

        return new RecoveryLaunchArguments(
            arguments[1],
            arguments[2],
            workerId,
            workerStartTimeUtcTicks);
    }

    private static void ValidateExactWorkerProcess(int workerId, long workerStartTimeUtcTicks)
    {
        using Process worker = GetExactParentProcess(workerId, workerStartTimeUtcTicks);
        string expectedPath = Environment.ProcessPath ?? string.Empty;
        string actualPath = worker.MainModule?.FileName ?? string.Empty;
        if (!Path.IsPathFullyQualified(expectedPath) ||
            !Path.IsPathFullyQualified(actualPath) ||
            !string.Equals(
                Path.GetFullPath(actualPath),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The shutdown recovery worker is not the exact current ZARA executable.");
        }
    }

    private static Process GetExactParentProcess(int processId, long startTimeUtcTicks)
    {
        Process process = Process.GetProcessById(processId);
        try
        {
            if (process.StartTime.ToUniversalTime().Ticks != startTimeUtcTicks)
            {
                throw new InvalidOperationException(
                    "The shutdown guard parent process identity no longer matches.");
            }

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static void ConfigureFirstApplicationShutdownNotification()
    {
        int succeeded = SetProcessShutdownParameters(
            ApplicationFirstShutdownLevel,
            flags: 0);
        if (succeeded == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Windows rejected the shutdown guard priority.");
        }
    }

    private enum ParentRecoveryOutcome
    {
        Recovered,
        Failed,
        Unavailable,
    }

    internal enum RecoveryAttemptOutcome
    {
        Recovered,
        RetryableFailure,
        FailedChildStillRunning,
    }

    private readonly record struct ParentMessageRead(string Message, bool IsAvailable)
    {
        internal static ParentMessageRead Unavailable { get; } = new(string.Empty, IsAvailable: false);
    }

    private sealed record ParentRecoveryAttempt(
        ParentRecoveryOutcome Outcome,
        bool FallbackRecoveryRequired);

    private sealed record RecoveryLaunchArguments(
        string PipeName,
        string Token,
        int WorkerId,
        long WorkerStartTimeUtcTicks);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetProcessShutdownParameters(uint level, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static partial int PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    private sealed class ShutdownGuardWindow : NativeWindow, IDisposable
    {
        private const uint WmQueryEndSession = 0x0011;
        private const uint WmEndSession = 0x0016;
        private const uint WmExitMessageLoop = 0x8001;
        private readonly TaskCompletionSource<bool> _sessionEnd = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _queryReceived;
        private int _disposed;

        internal ShutdownGuardWindow()
        {
            var createParams = new CreateParams
            {
                Caption = "ZARA Shutdown Guard",
                Style = unchecked((int)0x80000000),
                ExStyle = 0x08000080,
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                Parent = nint.Zero,
            };
            CreateHandle(createParams);
        }

        internal Task<bool> SessionEnd => _sessionEnd.Task;

        internal bool QueryReceived => Volatile.Read(ref _queryReceived) != 0;

        internal void RequestMessageLoopExit()
        {
            if (Handle == nint.Zero)
            {
                return;
            }

            _ = PostMessage(Handle, WmExitMessageLoop, nint.Zero, nint.Zero);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            DestroyHandle();
            GC.SuppressFinalize(this);
        }

        protected override void WndProc(ref Message message)
        {
            switch ((uint)message.Msg)
            {
                case WmQueryEndSession:
                    Volatile.Write(ref _queryReceived, 1);
                    message.Result = new nint(1);
                    return;
                case WmEndSession:
                    _sessionEnd.TrySetResult(message.WParam != nint.Zero);
                    message.Result = nint.Zero;
                    return;
                case WmExitMessageLoop:
                    System.Windows.Forms.Application.ExitThread();
                    message.Result = nint.Zero;
                    return;
                default:
                    base.WndProc(ref message);
                    return;
            }
        }
    }
}
