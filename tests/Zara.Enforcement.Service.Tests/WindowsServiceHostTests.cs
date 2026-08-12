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
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            new LogonSessionId(LowPart: 10, HighPart: 20));

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
        var ended = new ActiveConsoleSessionTarget(
            SessionId: 12,
            userSid,
            new LogonSessionId(LowPart: 10, HighPart: 20));
        var next = new ActiveConsoleSessionTarget(
            SessionId: 13,
            userSid,
            new LogonSessionId(LowPart: 11, HighPart: 20));
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
                CancellationToken.None);

        Assert.AreEqual(next, actual);
        Assert.AreEqual(2, delays);
    }

    [TestMethod]
    public async Task WaitForNextActiveSessionDoesNotAcceptSameLogonAfterTransientGap()
    {
        var target = new ActiveConsoleSessionTarget(
            SessionId: 12,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            new LogonSessionId(LowPart: 10, HighPart: 20));
        var next = target with
        {
            AuthenticationId = new LogonSessionId(LowPart: 11, HighPart: 20),
        };
        var readings = new Queue<ActiveConsoleSessionTarget?>([null, target, next]);
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
                CancellationToken.None);

        Assert.AreEqual(next, actual);
        Assert.AreEqual(2, delays);
    }

    [TestMethod]
    public async Task TargetLogoffEndsOnlyMatchingGeneration()
    {
        var target = new ActiveConsoleSessionTarget(
            SessionId: 12,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            new LogonSessionId(LowPart: 10, HighPart: 20));
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
    public void LogoffBetweenTargetCaptureAndActivationRejectsGeneration()
    {
        var coordinator = new ActiveConsoleGenerationCoordinator();
        SessionLifecycleSnapshot snapshot = coordinator.TryCapture(sessionId: 12) ??
            throw new AssertFailedException("An unknown session should be selectable.");
        var target = new ActiveConsoleSessionTarget(
            SessionId: 12,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            new LogonSessionId(LowPart: 10, HighPart: 20),
            snapshot.Version);
        var commandSource = new LatestSupervisionCommandSource(
            WindowsServiceHost.CreateInitialSupervisionDirective());
        var generation = new ActiveConsoleGeneration(target, commandSource);

        coordinator.ObserveSessionChange(sessionId: 12, isLoggedOn: false);

        Assert.IsFalse(coordinator.TryActivate(generation, snapshot.Version));
    }

    [TestMethod]
    public void NewLogonAfterLogoffCanActivateSameSessionId()
    {
        var coordinator = new ActiveConsoleGenerationCoordinator();
        coordinator.ObserveSessionChange(sessionId: 12, isLoggedOn: false);
        Assert.IsNull(coordinator.TryCapture(sessionId: 12));

        coordinator.ObserveSessionChange(sessionId: 12, isLoggedOn: true);
        SessionLifecycleSnapshot snapshot = coordinator.TryCapture(sessionId: 12) ??
            throw new AssertFailedException("The new logon should be selectable.");
        var target = new ActiveConsoleSessionTarget(
            SessionId: 12,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            new LogonSessionId(LowPart: 11, HighPart: 20),
            snapshot.Version);
        var generation = new ActiveConsoleGeneration(
            target,
            new LatestSupervisionCommandSource(
                WindowsServiceHost.CreateInitialSupervisionDirective()));

        Assert.IsTrue(coordinator.TryActivate(generation, snapshot.Version));
        coordinator.Clear(generation);
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
    public async Task AlreadyEndedGenerationDoesNotStartComponents()
    {
        int starts = 0;

        Task StartComponent(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref starts);
            return Task.CompletedTask;
        }

        Assert.IsTrue(await WindowsServiceHost.RunGenerationComponentsAsync(
            StartComponent,
            StartComponent,
            Task.CompletedTask,
            CancellationToken.None));
        Assert.AreEqual(0, starts);
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
        var first = new ActiveConsoleSessionTarget(
            SessionId: 12,
            userSid,
            new LogonSessionId(LowPart: 10, HighPart: 20));
        var second = new ActiveConsoleSessionTarget(
            SessionId: 13,
            userSid,
            new LogonSessionId(LowPart: 11, HighPart: 20));
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
                        SessionEnded: firstGeneration));
            },
            CancellationToken.None);

        Assert.HasCount(2, startedSessions);
        Assert.AreEqual(12, startedSessions[0]);
        Assert.AreEqual(13, startedSessions[1]);
    }
}
