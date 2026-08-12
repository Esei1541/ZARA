using System.Security.Principal;
using Zara.Enforcement.Service;
using Zara.Enforcement.Service.Supervision;

namespace Zara.Enforcement.Service.Tests;

[TestClass]
public sealed class WindowsServiceHostTests
{
    [TestMethod]
    public void InitialDirectiveRequiresFirstDesktopLaunchAfterInteractiveLogon()
    {
        SupervisionDirective directive = WindowsServiceHost.CreateInitialSupervisionDirective();

        Assert.AreEqual(0, directive.Revision);
        Assert.IsTrue(directive.RestartRequired);
        Assert.AreEqual(SupervisionDirectiveReason.ServiceStarted, directive.Reason);
    }

    [TestMethod]
    public async Task WaitForActiveSessionKeepsServiceIdleUntilTargetExists()
    {
        int reads = 0;
        int delays = 0;
        var expected = new ActiveConsoleSessionTarget(
            SessionId: 12,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null));

        ActiveConsoleSessionTarget actual = await WindowsServiceHost
            .WaitForActiveConsoleSessionAsync(
                () => Interlocked.Increment(ref reads) < 3 ? null : expected,
                (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref delays);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

        Assert.AreEqual(expected, actual);
        Assert.AreEqual(3, reads);
        Assert.AreEqual(2, delays);
    }

    [TestMethod]
    public async Task WaitForActiveSessionStopsWhileIdle()
    {
        using var cancellation = new CancellationTokenSource();
        var delayStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ActiveConsoleSessionTarget> wait = WindowsServiceHost
            .WaitForActiveConsoleSessionAsync(
                () => null,
                async (_, cancellationToken) =>
                {
                    delayStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
                cancellation.Token);
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => wait);
    }

    [TestMethod]
    public async Task WaitForNextActiveSessionSkipsEndedSessionAndAcceptsNewSession()
    {
        var userSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinUsersSid,
            domainSid: null);
        var ended = new ActiveConsoleSessionTarget(SessionId: 12, userSid);
        var next = new ActiveConsoleSessionTarget(SessionId: 13, userSid);
        var readings = new Queue<ActiveConsoleSessionTarget?>([ended, ended, next]);
        int delays = 0;

        ActiveConsoleSessionTarget actual = await WindowsServiceHost
            .WaitForNextActiveConsoleSessionAsync(
                () => readings.Dequeue(),
                (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref delays);
                    return Task.CompletedTask;
                },
                ended,
                previouslyEndedTargetWasUnavailable: false,
                CancellationToken.None);

        Assert.AreEqual(next, actual);
        Assert.AreEqual(2, delays);
    }

    [TestMethod]
    public async Task WaitForNextActiveSessionAcceptsSameTargetAfterUnavailableGap()
    {
        var target = new ActiveConsoleSessionTarget(
            SessionId: 12,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null));
        var readings = new Queue<ActiveConsoleSessionTarget?>([null, target]);
        int delays = 0;

        ActiveConsoleSessionTarget actual = await WindowsServiceHost
            .WaitForNextActiveConsoleSessionAsync(
                () => readings.Dequeue(),
                (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref delays);
                    return Task.CompletedTask;
                },
                target,
                previouslyEndedTargetWasUnavailable: false,
                CancellationToken.None);

        Assert.AreEqual(target, actual);
        Assert.AreEqual(1, delays);
    }

    [TestMethod]
    public async Task TargetLogoffEndsOnlyMatchingGeneration()
    {
        var target = new ActiveConsoleSessionTarget(
            SessionId: 12,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null));
        var commandSource = new LatestSupervisionCommandSource(
            WindowsServiceHost.CreateInitialSupervisionDirective());
        var generation = new ActiveConsoleGeneration(target, commandSource);

        Assert.IsFalse(generation.TryEndSession(sessionId: 13));
        Assert.IsFalse(generation.SessionEnded.IsCompleted);
        Assert.IsFalse(commandSource.IsSessionEnding);

        Assert.IsTrue(generation.TryEndSession(sessionId: 12));
        await generation.SessionEnded.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(commandSource.IsSessionEnding);
        Assert.IsFalse(commandSource.Current.RestartRequired);
        Assert.AreEqual(
            SupervisionDirectiveReason.SessionEnding,
            commandSource.Current.Reason);
        Assert.IsTrue(generation.TryEndSession(sessionId: 12));
    }

    [TestMethod]
    public async Task SessionEndCancelsBothComponentsAndWaitsForCleanup()
    {
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bothCleaningUp = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionEnded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        int cleaningUp = 0;
        int cleaned = 0;

        async Task RunComponent(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (Interlocked.Increment(ref cleaningUp) == 2)
                {
                    bothCleaningUp.TrySetResult();
                }

                await allowCleanup.Task;
                Interlocked.Increment(ref cleaned);
            }
        }

        Task<bool> run = WindowsServiceHost.RunGenerationComponentsAsync(
            RunComponent,
            RunComponent,
            sessionEnded.Task,
            CancellationToken.None);
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        sessionEnded.TrySetResult();
        await bothCleaningUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(run.IsCompleted);

        allowCleanup.TrySetResult();
        Assert.IsTrue(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(2, cleaned);
    }

    [TestMethod]
    public async Task ComponentFailureCancelsPeerAndPropagatesFailure()
    {
        var peerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failComponent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var peerCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunFailingComponent(CancellationToken cancellationToken)
        {
            await failComponent.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("generation failed");
        }

        async Task RunPeer(CancellationToken cancellationToken)
        {
            peerStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                peerCancelled.TrySetResult();
                throw;
            }
        }

        Task<bool> run = WindowsServiceHost.RunGenerationComponentsAsync(
            RunFailingComponent,
            RunPeer,
            Task.Delay(Timeout.InfiniteTimeSpan),
            CancellationToken.None);
        await peerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        failComponent.TrySetResult();

        InvalidOperationException actual = await Assert
            .ThrowsExactlyAsync<InvalidOperationException>(() => run);
        Assert.AreEqual("generation failed", actual.Message);
        await peerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task SessionLoopStartsNewGenerationForNextLogin()
    {
        var userSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinUsersSid,
            domainSid: null);
        var first = new ActiveConsoleSessionTarget(SessionId: 12, userSid);
        var second = new ActiveConsoleSessionTarget(SessionId: 13, userSid);
        var targets = new Queue<ActiveConsoleSessionTarget?>([first, second]);
        var startedSessions = new List<int>();

        await WindowsServiceHost.RunSessionGenerationsAsync(
            () => targets.Dequeue(),
            (_, _) => throw new AssertFailedException(
                "Different session IDs should not require a polling delay."),
            (target, _) =>
            {
                startedSessions.Add(target.SessionId);
                bool firstGeneration = startedSessions.Count == 1;
                return Task.FromResult(
                    new SessionGenerationRunResult(
                        SessionEnded: firstGeneration,
                        TargetWasObservedUnavailable: false));
            },
            CancellationToken.None);

        Assert.HasCount(2, startedSessions);
        Assert.AreEqual(12, startedSessions[0]);
        Assert.AreEqual(13, startedSessions[1]);
    }
}
