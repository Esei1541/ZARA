using System.Diagnostics;
using Zara.Infrastructure.Windows;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class RecoveryLaunchRetryTests
{
    private static readonly TimeSpan ExpectedRetryDelay = TimeSpan.FromMilliseconds(25);

    [TestMethod]
    public async Task FirstSuccessfulAttemptReturnsWithoutDelayOrRetry()
    {
        int attemptCount = 0;
        int delayCount = 0;

        bool recovered = await WindowsShutdownGuardProcess.ExecuteRecoveryLaunchRetryAsync(
            () =>
            {
                attemptCount++;
                return Task.FromResult(
                    WindowsShutdownGuardProcess.RecoveryAttemptOutcome.Recovered);
            },
            attemptCount: 3,
            ExpectedRetryDelay,
            _ =>
            {
                delayCount++;
                return Task.CompletedTask;
            });

        Assert.IsTrue(recovered);
        Assert.AreEqual(1, attemptCount);
        Assert.AreEqual(0, delayCount);
    }

    [TestMethod]
    public async Task RetryableFailureThenSuccessRetriesOnce()
    {
        int attemptCount = 0;
        var observedDelays = new List<TimeSpan>();

        bool recovered = await WindowsShutdownGuardProcess.ExecuteRecoveryLaunchRetryAsync(
            () => Task.FromResult(++attemptCount == 1
                ? WindowsShutdownGuardProcess.RecoveryAttemptOutcome.RetryableFailure
                : WindowsShutdownGuardProcess.RecoveryAttemptOutcome.Recovered),
            attemptCount: 3,
            ExpectedRetryDelay,
            delay =>
            {
                observedDelays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.IsTrue(recovered);
        Assert.AreEqual(2, attemptCount);
        CollectionAssert.AreEqual(
            new[] { ExpectedRetryDelay },
            observedDelays);
    }

    [TestMethod]
    public async Task AttemptExceptionIsRetriedOnlyAfterCleanupIsConfirmed()
    {
        int attemptCount = 0;
        int cleanupCount = 0;
        int delayCount = 0;

        bool recovered = await WindowsShutdownGuardProcess.ExecuteRecoveryLaunchRetryAsync(
            async () =>
            {
                attemptCount++;
                try
                {
                    if (attemptCount == 1)
                    {
                        throw new InvalidOperationException("Transient launch failure.");
                    }

                    return WindowsShutdownGuardProcess.RecoveryAttemptOutcome.Recovered;
                }
                catch (InvalidOperationException)
                {
                    cleanupCount++;
                    await Task.Yield();
                    return WindowsShutdownGuardProcess.RecoveryAttemptOutcome.RetryableFailure;
                }
            },
            attemptCount: 3,
            ExpectedRetryDelay,
            _ =>
            {
                delayCount++;
                return Task.CompletedTask;
            });

        Assert.IsTrue(recovered);
        Assert.AreEqual(2, attemptCount);
        Assert.AreEqual(1, cleanupCount);
        Assert.AreEqual(1, delayCount);
    }

    [TestMethod]
    public async Task UnclassifiedAttemptExceptionStopsWithoutRetry()
    {
        int attemptCount = 0;
        int delayCount = 0;
        var expectedException = new InvalidOperationException("Cleanup outcome is unknown.");

        InvalidOperationException actualException =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => WindowsShutdownGuardProcess.ExecuteRecoveryLaunchRetryAsync(
                    () =>
                    {
                        attemptCount++;
                        return Task.FromException<
                            WindowsShutdownGuardProcess.RecoveryAttemptOutcome>(expectedException);
                    },
                    attemptCount: 3,
                    ExpectedRetryDelay,
                    _ =>
                    {
                        delayCount++;
                        return Task.CompletedTask;
                    }));

        Assert.AreSame(expectedException, actualException);
        Assert.AreEqual(1, attemptCount);
        Assert.AreEqual(0, delayCount);
    }

    [TestMethod]
    public async Task RetryableFailuresExhaustConfiguredAttempts()
    {
        int attemptCount = 0;
        int delayCount = 0;

        bool recovered = await WindowsShutdownGuardProcess.ExecuteRecoveryLaunchRetryAsync(
            () =>
            {
                attemptCount++;
                return Task.FromResult(
                    WindowsShutdownGuardProcess.RecoveryAttemptOutcome.RetryableFailure);
            },
            attemptCount: 3,
            ExpectedRetryDelay,
            _ =>
            {
                delayCount++;
                return Task.CompletedTask;
            });

        Assert.IsFalse(recovered);
        Assert.AreEqual(3, attemptCount);
        Assert.AreEqual(2, delayCount);
    }

    [TestMethod]
    public async Task NextAttemptWaitsForPreviousAttemptOutcomeAndRetryDelay()
    {
        var firstAttemptCompletion = new TaskCompletionSource<
            WindowsShutdownGuardProcess.RecoveryAttemptOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int attemptCount = 0;

        Task<bool> recovery = WindowsShutdownGuardProcess.ExecuteRecoveryLaunchRetryAsync(
            () => ++attemptCount == 1
                ? firstAttemptCompletion.Task
                : Task.FromResult(
                    WindowsShutdownGuardProcess.RecoveryAttemptOutcome.Recovered),
            attemptCount: 2,
            ExpectedRetryDelay,
            delay =>
            {
                Assert.AreEqual(ExpectedRetryDelay, delay);
                delayStarted.TrySetResult(true);
                return releaseDelay.Task;
            });

        Assert.AreEqual(1, attemptCount);
        Assert.IsFalse(delayStarted.Task.IsCompleted);

        firstAttemptCompletion.SetResult(
            WindowsShutdownGuardProcess.RecoveryAttemptOutcome.RetryableFailure);
        await delayStarted.Task;

        Assert.AreEqual(1, attemptCount);
        releaseDelay.SetResult(true);

        Assert.IsTrue(await recovery);
        Assert.AreEqual(2, attemptCount);
    }

    [TestMethod]
    public async Task FailedChildStillRunningStopsFurtherAttempts()
    {
        int attemptCount = 0;
        int delayCount = 0;

        bool recovered = await WindowsShutdownGuardProcess.ExecuteRecoveryLaunchRetryAsync(
            () =>
            {
                attemptCount++;
                return Task.FromResult(
                    WindowsShutdownGuardProcess.RecoveryAttemptOutcome.FailedChildStillRunning);
            },
            attemptCount: 3,
            ExpectedRetryDelay,
            _ =>
            {
                delayCount++;
                return Task.CompletedTask;
            });

        Assert.IsFalse(recovered);
        Assert.AreEqual(1, attemptCount);
        Assert.AreEqual(0, delayCount);
    }

    [TestMethod]
    public async Task FailedRecoveryCleanupStopsTheExactOwnedChild()
    {
        string powershellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        using Process child = Process.Start(startInfo) ??
            throw new InvalidOperationException("The owned cleanup test child did not start.");

        try
        {
            Assert.IsFalse(child.HasExited);

            bool stopped = await WindowsShutdownGuardProcess
                .StopFailedRecoveryProcessAsync(child);

            Assert.IsTrue(stopped);
            Assert.IsTrue(child.HasExited);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: false);
                await child.WaitForExitAsync();
            }
        }
    }
}
