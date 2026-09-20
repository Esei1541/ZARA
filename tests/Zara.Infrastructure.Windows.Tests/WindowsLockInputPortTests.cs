using Zara.Application.Locking;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsLockInputPortTests
{
    [TestMethod]
    public async Task UnlockedAdapterCreatesNoHookAndRepeatedLockUsesOneHook()
    {
        var hooks = new List<RecordingHook>();
        using var port = new WindowsLockInputPort(() =>
        {
            var hook = new RecordingHook();
            hooks.Add(hook);
            return hook;
        });

        await port.DisableAsync(CancellationToken.None);
        Assert.IsEmpty(hooks);
        await port.EnableAsync(CancellationToken.None);
        await port.EnableAsync(CancellationToken.None);
        Assert.HasCount(1, hooks);
        Assert.AreEqual(1, hooks[0].Starts);

        await port.DisableAsync(CancellationToken.None);
        await port.DisableAsync(CancellationToken.None);
        Assert.AreEqual(1, hooks[0].Stops);
        await port.EnableAsync(CancellationToken.None);
        Assert.HasCount(2, hooks);
    }

    [TestMethod]
    public async Task UnlockWaitsForStartupAndForHookCleanup()
    {
        var hook = new RecordingHook { CompleteStart = false, CompleteStop = false };
        using var port = new WindowsLockInputPort(() => hook);
        Task locking = port.EnableAsync(CancellationToken.None);
        Task unlocking = port.DisableAsync(CancellationToken.None);
        Assert.IsFalse(locking.IsCompleted);
        Assert.IsFalse(unlocking.IsCompleted);
        Assert.AreEqual(0, hook.Stops);

        hook.StartedSource.SetResult();
        await locking;
        await hook.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(unlocking.IsCompleted);
        hook.StoppedSource.SetResult();
        await unlocking;
    }

    [TestMethod]
    public async Task CancelledLockDoesNotStartHook()
    {
        var hook = new RecordingHook();
        using var port = new WindowsLockInputPort(() => hook);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => port.EnableAsync(cancellation.Token));

        Assert.AreEqual(0, hook.Starts);
    }

    [TestMethod]
    public async Task CancellationAfterStartingDoesNotOrphanHook()
    {
        var hook = new RecordingHook { CompleteStart = false };
        using var port = new WindowsLockInputPort(() => hook);
        using var cancellation = new CancellationTokenSource();
        Task locking = port.EnableAsync(cancellation.Token);

        cancellation.Cancel();
        Assert.IsFalse(locking.IsCompleted);
        hook.StartedSource.SetResult();
        await locking;
        await port.DisableAsync(CancellationToken.None);

        Assert.AreEqual(1, hook.Stops);
        Assert.IsTrue(hook.Stopped.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task StartupFailureLeavesUnlockAvailableAndAllowsRetry()
    {
        var failed = new RecordingHook { CompleteStart = false };
        failed.StartedSource.SetException(new InvalidOperationException("Install failed."));
        failed.StoppedSource.SetResult();
        var replacement = new RecordingHook();
        int created = 0;
        using var port = new WindowsLockInputPort(() => created++ == 0 ? failed : replacement);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => port.EnableAsync(CancellationToken.None));
        await port.DisableAsync(CancellationToken.None);
        await port.EnableAsync(CancellationToken.None);

        Assert.AreEqual(1, replacement.Starts);
    }

    [TestMethod]
    public async Task StopFailureMustBeRetriedBeforeRelocking()
    {
        var first = new RecordingHook { FailStop = true };
        var replacement = new RecordingHook();
        int created = 0;
        using var port = new WindowsLockInputPort(() => created++ == 0 ? first : replacement);
        await port.EnableAsync(CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => port.DisableAsync(CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => port.EnableAsync(CancellationToken.None));
        Assert.AreEqual(0, replacement.Starts);

        first.FailStop = false;
        await port.EnableAsync(CancellationToken.None);
        Assert.AreEqual(1, replacement.Starts);
        Assert.IsTrue(first.Stopped.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task DisposeRestoresInputAndPreventsLaterEnable()
    {
        var hook = new RecordingHook();
        var port = new WindowsLockInputPort(() => hook);
        await port.EnableAsync(CancellationToken.None);

        port.Dispose();
        port.Dispose();

        Assert.AreEqual(1, hook.Stops);
        Assert.IsTrue(hook.Stopped.IsCompletedSuccessfully);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => port.EnableAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task RestrictionUsesSameUnlockAndDisposePathsAsInput()
    {
        var restriction = new RecordingRestriction();
        var hook = new RecordingHook();
        var port = new WindowsLockInputPort(() => hook, restriction);
        await port.DisableAsync(CancellationToken.None);
        Assert.AreEqual(0, restriction.Restores);

        await port.EnableAsync(CancellationToken.None);
        Assert.AreEqual(1, restriction.Applies);
        await port.DisableAsync(CancellationToken.None);
        Assert.AreEqual(1, restriction.Restores);
        port.Dispose();
        Assert.AreEqual(1, restriction.Restores);

        using var secondPort = new WindowsLockInputPort(() => new RecordingHook(), restriction);
        await secondPort.EnableAsync(CancellationToken.None);
        secondPort.Dispose();
        Assert.AreEqual(2, restriction.Restores);
    }

    [TestMethod]
    public async Task FailedPolicyAcknowledgementStillRestoresOnUnlock()
    {
        var restriction = new RecordingRestriction { FailApply = true };
        var hook = new RecordingHook();
        using var port = new WindowsLockInputPort(() => hook, restriction);

        await Assert.ThrowsExactlyAsync<IOException>(() => port.EnableAsync(CancellationToken.None));
        await port.DisableAsync(CancellationToken.None);

        Assert.AreEqual(1, hook.Stops);
        Assert.AreEqual(1, restriction.Restores);
    }

    [TestMethod]
    public async Task RepeatedEnableKeepsSuccessfulRestrictionUntilUnlock()
    {
        var restriction = new RecordingRestriction();
        using var port = new WindowsLockInputPort(() => new RecordingHook(), restriction);

        await port.EnableAsync(CancellationToken.None);
        await port.EnableAsync(CancellationToken.None);
        Assert.AreEqual(1, restriction.Applies);
        Assert.AreEqual(0, restriction.Restores);

        await port.DisableAsync(CancellationToken.None);
        await port.EnableAsync(CancellationToken.None);
        Assert.AreEqual(2, restriction.Applies);
        Assert.AreEqual(1, restriction.Restores);
    }

    [TestMethod]
    public async Task FailedRestrictionRetriesWithoutRestartingSuccessfulHook()
    {
        var restriction = new RecordingRestriction { FailApply = true };
        var hook = new RecordingHook();
        using var port = new WindowsLockInputPort(() => hook, restriction);

        await Assert.ThrowsExactlyAsync<IOException>(() => port.EnableAsync(CancellationToken.None));
        Assert.AreEqual(1, hook.Starts);
        Assert.AreEqual(0, hook.Stops);
        Assert.AreEqual(0, restriction.Restores);

        restriction.FailApply = false;
        await port.EnableAsync(CancellationToken.None);
        await port.EnableAsync(CancellationToken.None);

        Assert.AreEqual(2, restriction.Applies);
        Assert.AreEqual(1, hook.Starts);
        Assert.AreEqual(0, hook.Stops);
    }

    [TestMethod]
    public async Task FailedHookStillAppliesRestrictionAndRetriesOnlyHook()
    {
        var failed = new RecordingHook { CompleteStart = false };
        failed.StartedSource.SetException(new InvalidOperationException("Install failed."));
        var replacement = new RecordingHook();
        var restriction = new RecordingRestriction();
        int created = 0;
        using var port = new WindowsLockInputPort(() => created++ == 0 ? failed : replacement, restriction);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => port.EnableAsync(CancellationToken.None));
        Assert.AreEqual(1, failed.Stops);
        Assert.AreEqual(1, restriction.Applies);
        Assert.AreEqual(0, restriction.Restores);

        await port.EnableAsync(CancellationToken.None);

        Assert.AreEqual(1, replacement.Starts);
        Assert.AreEqual(1, restriction.Applies);
        Assert.AreEqual(0, restriction.Restores);
    }

    [TestMethod]
    public async Task HookCreationFailureStillAppliesAndRestoresRestriction()
    {
        var restriction = new RecordingRestriction();
        using var port = new WindowsLockInputPort(
            () => throw new InvalidOperationException("Hook could not be created."),
            restriction);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => port.EnableAsync(CancellationToken.None));
        Assert.AreEqual(1, restriction.Applies);
        await port.DisableAsync(CancellationToken.None);
        Assert.AreEqual(1, restriction.Restores);
    }

    [TestMethod]
    public async Task BothEnableFailuresAreReportedAndUnacknowledgedRestrictionIsRestored()
    {
        var failed = new RecordingHook { CompleteStart = false };
        failed.StartedSource.SetException(new InvalidOperationException("Install failed."));
        var restriction = new RecordingRestriction { FailApply = true };
        using var port = new WindowsLockInputPort(() => failed, restriction);

        AggregateException failure = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => port.EnableAsync(CancellationToken.None));

        Assert.HasCount(2, failure.InnerExceptions);
        Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerExceptions[0]);
        Assert.IsInstanceOfType<IOException>(failure.InnerExceptions[1]);
        await port.DisableAsync(CancellationToken.None);
        Assert.AreEqual(1, restriction.Restores);
    }

    [TestMethod]
    public async Task FailedRestoreDoesNotReuseEarlierRestrictionSuccess()
    {
        var restriction = new RecordingRestriction { FailRestore = true };
        using var port = new WindowsLockInputPort(() => new RecordingHook(), restriction);
        await port.EnableAsync(CancellationToken.None);

        await Assert.ThrowsExactlyAsync<IOException>(() => port.DisableAsync(CancellationToken.None));
        await port.EnableAsync(CancellationToken.None);

        Assert.AreEqual(2, restriction.Applies);
        restriction.FailRestore = false;
        await port.DisableAsync(CancellationToken.None);
        Assert.AreEqual(2, restriction.Restores);
    }

    [TestMethod]
    public async Task StoppedHookIsReplacedWithoutReapplyingSuccessfulRestriction()
    {
        var stopped = new RecordingHook();
        var replacement = new RecordingHook();
        var restriction = new RecordingRestriction();
        int created = 0;
        using var port = new WindowsLockInputPort(() => created++ == 0 ? stopped : replacement, restriction);
        await port.EnableAsync(CancellationToken.None);
        stopped.StoppedSource.SetResult();

        await port.EnableAsync(CancellationToken.None);

        Assert.AreEqual(1, replacement.Starts);
        Assert.AreEqual(1, restriction.Applies);
        Assert.AreEqual(0, restriction.Restores);
    }

    [TestMethod]
    public async Task FailedPolicyAcknowledgementStillRestoresOnDispose()
    {
        var restriction = new RecordingRestriction { FailApply = true };
        var hook = new RecordingHook();
        using var port = new WindowsLockInputPort(() => hook, restriction);

        await Assert.ThrowsExactlyAsync<IOException>(() => port.EnableAsync(CancellationToken.None));
        port.Dispose();

        Assert.AreEqual(1, hook.Stops);
        Assert.AreEqual(1, restriction.Restores);
    }

    [TestMethod]
    public async Task HookStopFailureStillRestoresPolicyAndPolicyFailureCanBeRetried()
    {
        var restriction = new RecordingRestriction();
        var hook = new RecordingHook { FailStop = true };
        using var port = new WindowsLockInputPort(() => hook, restriction);
        await port.EnableAsync(CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => port.DisableAsync(CancellationToken.None));
        Assert.AreEqual(1, restriction.Restores);
        hook.FailStop = false;
        await port.EnableAsync(CancellationToken.None);
        restriction.FailRestore = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => port.DisableAsync(CancellationToken.None));
        restriction.FailRestore = false;
        await port.DisableAsync(CancellationToken.None);
        Assert.AreEqual(3, restriction.Restores);
    }

    private sealed class RecordingRestriction : ILockInputPort
    {
        public int Applies { get; private set; }
        public int Restores { get; private set; }
        public bool FailApply { get; set; }
        public bool FailRestore { get; set; }

        public Task EnableAsync(CancellationToken cancellationToken)
        {
            Applies++;
            return FailApply ? Task.FromException(new IOException("Apply failed.")) : Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken)
        {
            Restores++;
            return FailRestore ? Task.FromException(new IOException("Restore failed.")) : Task.CompletedTask;
        }
    }

    private sealed class RecordingHook : IShellShortcutHook
    {
        internal TaskCompletionSource StartedSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StoppedSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CompleteStart { get; init; } = true;
        internal bool CompleteStop { get; init; } = true;
        internal bool FailStop { get; set; }
        internal int Starts { get; private set; }
        internal int Stops { get; private set; }
        public Task Started => StartedSource.Task;
        public Task Stopped => StoppedSource.Task;

        public void Start()
        {
            Starts++;
            if (CompleteStart)
            {
                StartedSource.TrySetResult();
            }
        }

        public void Stop()
        {
            Stops++;
            StopRequested.TrySetResult();
            if (FailStop)
            {
                throw new InvalidOperationException("Stop failed.");
            }

            if (CompleteStop)
            {
                StoppedSource.TrySetResult();
            }
        }
    }
}
