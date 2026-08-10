using System.Collections.Concurrent;
using System.Security.Principal;
using Zara.Enforcement.Service.Supervision;
using Zara.Supervision.Contracts;

namespace Zara.Enforcement.Service.Tests;

[TestClass]
public sealed class WindowsSupervisionPipeServerTests
{
    private const int SessionId = 7;

    [TestMethod]
    public async Task RegisteredWriteFailureRestoresRequiredRecoveryDirective()
    {
        TestContext context = await CreateRecoveryContextAsync();
        await using IDesktopLaunchHandshake handshake = await context.Registry.CreateAsync(
            SessionId,
            CancellationToken.None);
        Assert.IsTrue(handshake.TryBindProcess(processId: 102));
        var channel = new ScriptedChannel(failWriteNumber: 1);
        var lifetime = new FakeDesktopLifetime(processId: 102);
        lifetime.Exit(exitCode: 1);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            context.Server.HandleAuthenticatedConnectionAsync(
                channel,
                lifetime,
                CreateRegistration(
                    processId: 102,
                    restartRequired: true,
                    recoverLock: true,
                    handshake.OneTimeToken),
                CancellationToken.None));

        Assert.IsTrue(context.Source.Current.RestartRequired);
        Assert.AreEqual(SupervisionDirectiveReason.LeaseUpdated, context.Source.Current.Reason);
    }

    [TestMethod]
    public async Task TokenlessRegistrationCannotReplaceRequiredRecoveryLease()
    {
        TestContext context = await CreateRecoveryContextAsync();
        var channel = new ScriptedChannel();
        var lifetime = new FakeDesktopLifetime(processId: 103);

        await context.Server.HandleAuthenticatedConnectionAsync(
            channel,
            lifetime,
            CreateRegistration(
                processId: 103,
                restartRequired: false,
                recoverLock: false,
                launchToken: null),
            CancellationToken.None);

        SupervisionResponse response = AssertSingleResponse(channel);
        Assert.AreEqual(SupervisionResponseKind.Rejected, response.Kind);
        Assert.AreEqual("RECOVERY_TOKEN_REQUIRED", response.ErrorCode);
        Assert.IsTrue(context.Source.Current.RestartRequired);
        Assert.AreEqual(0, lifetime.WaitCount);
    }

    [TestMethod]
    public async Task TokenlessRegistrationCannotReplacePendingInitialServiceLaunch()
    {
        var source = CreateInitialLaunchSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry, requireInitialServiceLaunch: true);
        var channel = new ScriptedChannel();
        var lifetime = new FakeDesktopLifetime(processId: 114);

        await server.HandleAuthenticatedConnectionAsync(
            channel,
            lifetime,
            CreateRegistration(114, restartRequired: false, recoverLock: false, launchToken: null),
            CancellationToken.None);

        SupervisionResponse response = AssertSingleResponse(channel);
        Assert.AreEqual(SupervisionResponseKind.Rejected, response.Kind);
        Assert.AreEqual("SERVICE_LAUNCH_TOKEN_REQUIRED", response.ErrorCode);
        Assert.IsTrue(source.Current.RestartRequired);
        Assert.AreEqual(0, lifetime.WaitCount);
    }

    [TestMethod]
    public async Task InitialServiceLaunchAcceptsExactTokenAndPreservesExplicitExit()
    {
        var source = CreateInitialLaunchSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry, requireInitialServiceLaunch: true);
        await using IDesktopLaunchHandshake handshake = await registry.CreateAsync(
            SessionId,
            CancellationToken.None);
        Assert.IsTrue(handshake.TryBindProcess(processId: 115));

        var update = new SupervisionRequest(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.UpdateLease,
            Process: null,
            new SupervisionLease(1, true, false),
            Revision: null,
            LaunchToken: null);
        var release = new SupervisionRequest(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.ReleaseForExplicitExit,
            Process: null,
            new SupervisionLease(2, false, false),
            Revision: null,
            LaunchToken: null);
        var channel = new ScriptedChannel([
            update,
            CreateHealthyRequest(revision: 1),
            release,
        ]);
        var lifetime = new FakeDesktopLifetime(processId: 115);
        lifetime.Exit(SupervisionProtocol.ExplicitExitCode);

        await server.HandleAuthenticatedConnectionAsync(
            channel,
            lifetime,
            CreateRegistration(115, restartRequired: true, recoverLock: false, handshake.OneTimeToken),
            CancellationToken.None);

        SupervisionResponse[] responses = channel.Responses.ToArray();
        Assert.HasCount(4, responses);
        Assert.AreEqual(SupervisionResponseKind.Registered, responses[0].Kind);
        Assert.IsFalse(responses[0].RecoverLockOnStart);
        Assert.AreEqual(SupervisionResponseKind.LeaseAcknowledged, responses[1].Kind);
        Assert.AreEqual(SupervisionResponseKind.HealthyAcknowledged, responses[2].Kind);
        Assert.AreEqual(SupervisionResponseKind.ExitAcknowledged, responses[3].Kind);
        await handshake.WaitForHealthyAsync(CancellationToken.None);
        Assert.IsFalse(source.Current.RestartRequired);
        Assert.AreEqual(SupervisionDirectiveReason.ExplicitRelease, source.Current.Reason);
    }

    [TestMethod]
    public async Task UnhealthyInitialServiceLaunchRemainsRequiredAfterProcessExit()
    {
        var source = CreateInitialLaunchSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry, requireInitialServiceLaunch: true);
        await using IDesktopLaunchHandshake handshake = await registry.CreateAsync(
            SessionId,
            CancellationToken.None);
        Assert.IsTrue(handshake.TryBindProcess(processId: 116));

        var channel = new ScriptedChannel();
        var lifetime = new FakeDesktopLifetime(processId: 116);
        lifetime.Exit(exitCode: 1);

        await server.HandleAuthenticatedConnectionAsync(
            channel,
            lifetime,
            CreateRegistration(116, restartRequired: false, recoverLock: false, handshake.OneTimeToken),
            CancellationToken.None);

        Assert.AreEqual(SupervisionResponseKind.Registered, AssertSingleResponse(channel).Kind);
        Assert.IsTrue(source.Current.RestartRequired);
        Assert.AreEqual(SupervisionDirectiveReason.LeaseUpdated, source.Current.Reason);
    }

    [TestMethod]
    public async Task ExitAcknowledgementFailureDoesNotSuppressRequiredRecovery()
    {
        var source = CreateReleasedSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry);
        var release = new SupervisionRequest(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.ReleaseForExplicitExit,
            Process: null,
            new SupervisionLease(1, false, false),
            Revision: null,
            LaunchToken: null);
        var channel = new ScriptedChannel(new[] { release }, failWriteNumber: 2);
        var lifetime = new FakeDesktopLifetime(processId: 104);
        lifetime.Exit(SupervisionProtocol.ExplicitExitCode);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            server.HandleAuthenticatedConnectionAsync(
                channel,
                lifetime,
                CreateRegistration(104, restartRequired: true, recoverLock: true, null),
                CancellationToken.None));

        Assert.IsTrue(source.Current.RestartRequired);
        Assert.AreEqual(SupervisionDirectiveReason.LeaseUpdated, source.Current.Reason);
    }

    [TestMethod]
    public async Task DecreasingLeaseAcknowledgementFailureKeepsPreviousRequiredLease()
    {
        var source = CreateReleasedSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry);
        var decrease = new SupervisionRequest(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.UpdateLease,
            Process: null,
            new SupervisionLease(1, false, false),
            Revision: null,
            LaunchToken: null);
        var channel = new ScriptedChannel(new[] { decrease }, failWriteNumber: 2);
        var lifetime = new FakeDesktopLifetime(processId: 107);
        lifetime.Exit(exitCode: 1);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            server.HandleAuthenticatedConnectionAsync(
                channel,
                lifetime,
                CreateRegistration(107, restartRequired: true, recoverLock: true, null),
                CancellationToken.None));

        Assert.IsTrue(source.Current.RestartRequired);
        Assert.AreEqual(SupervisionDirectiveReason.LeaseUpdated, source.Current.Reason);
    }

    [TestMethod]
    public async Task PreparedDecreasingLeaseWithoutCommitKeepsPreviousRequiredLease()
    {
        var source = CreateReleasedSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry);
        var decrease = new SupervisionRequest(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.UpdateLease,
            Process: null,
            new SupervisionLease(1, false, false),
            Revision: null,
            LaunchToken: null);
        var channel = new ScriptedChannel(new[] { decrease });
        var lifetime = new FakeDesktopLifetime(processId: 108);
        lifetime.Exit(exitCode: 1);

        await server.HandleAuthenticatedConnectionAsync(
            channel,
            lifetime,
            CreateRegistration(108, restartRequired: true, recoverLock: true, null),
            CancellationToken.None);

        Assert.AreEqual(SupervisionResponseKind.LeasePrepared, channel.Responses.Last().Kind);
        Assert.IsTrue(source.Current.RestartRequired);
    }

    [TestMethod]
    public async Task PreparedDecreasingLeaseCommitsOnlyAfterRevisionConfirmation()
    {
        var source = CreateReleasedSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry);
        var decrease = new SupervisionRequest(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.UpdateLease,
            Process: null,
            new SupervisionLease(1, false, false),
            Revision: null,
            LaunchToken: null);
        var commit = new SupervisionRequest(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.CommitLease,
            Process: null,
            Lease: null,
            Revision: 1,
            LaunchToken: null);
        var channel = new ScriptedChannel(new[] { decrease, commit });
        var lifetime = new FakeDesktopLifetime(processId: 109);
        lifetime.Exit(exitCode: 1);

        await server.HandleAuthenticatedConnectionAsync(
            channel,
            lifetime,
            CreateRegistration(109, restartRequired: true, recoverLock: true, null),
            CancellationToken.None);

        Assert.IsFalse(source.Current.RestartRequired);
    }

    [TestMethod]
    public async Task IncreasingLeaseAcknowledgementFailureKeepsConservativeRequiredLease()
    {
        var source = CreateReleasedSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry);
        var increase = new SupervisionRequest(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.UpdateLease,
            Process: null,
            new SupervisionLease(1, true, true),
            Revision: null,
            LaunchToken: null);
        var channel = new ScriptedChannel(new[] { increase }, failWriteNumber: 2);
        var lifetime = new FakeDesktopLifetime(processId: 110);
        lifetime.Exit(exitCode: 1);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            server.HandleAuthenticatedConnectionAsync(
                channel,
                lifetime,
                CreateRegistration(110, restartRequired: false, recoverLock: false, null),
                CancellationToken.None));

        Assert.IsTrue(source.Current.RestartRequired);
    }

    [TestMethod]
    [DataRow(SupervisionProtocol.ExplicitExitCode, false)]
    [DataRow(1, true)]
    public async Task AcknowledgedReleaseRequiresExactMarkerExit(
        int exitCode,
        bool expectedRestart)
    {
        var source = CreateReleasedSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry);
        var release = new SupervisionRequest(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.ReleaseForExplicitExit,
            Process: null,
            new SupervisionLease(1, false, false),
            Revision: null,
            LaunchToken: null);
        var channel = new ScriptedChannel(new[] { release });
        var lifetime = new FakeDesktopLifetime(processId: 105);
        lifetime.Exit(exitCode);

        await server.HandleAuthenticatedConnectionAsync(
            channel,
            lifetime,
            CreateRegistration(105, restartRequired: true, recoverLock: true, null),
            CancellationToken.None);

        Assert.AreEqual(expectedRestart, source.Current.RestartRequired);
        Assert.AreEqual(
            expectedRestart
                ? SupervisionDirectiveReason.LeaseUpdated
                : SupervisionDirectiveReason.ExplicitRelease,
            source.Current.Reason);
    }

    [TestMethod]
    public async Task SessionEndingDominatesRequiredClientFinally()
    {
        var source = CreateReleasedSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry);
        var channel = new ScriptedChannel(completeReadsWhenEmpty: false);
        var lifetime = new FakeDesktopLifetime(processId: 106);
        Task handler = server.HandleAuthenticatedConnectionAsync(
            channel,
            lifetime,
            CreateRegistration(106, restartRequired: true, recoverLock: true, null),
            CancellationToken.None);
        await channel.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.EndSession();
        channel.CompleteReads();
        lifetime.Exit(exitCode: 1);
        await handler;

        Assert.IsFalse(source.Current.RestartRequired);
        Assert.AreEqual(SupervisionDirectiveReason.SessionEnding, source.Current.Reason);
        Assert.IsTrue(source.IsSessionEnding);
    }

    private static async Task<TestContext> CreateRecoveryContextAsync()
    {
        var source = CreateReleasedSource();
        var registry = new DesktopLaunchHandshakeRegistry();
        var server = CreateServer(source, registry);
        var seedChannel = new ScriptedChannel();
        var seedLifetime = new FakeDesktopLifetime(processId: 101);
        seedLifetime.Exit(exitCode: 1);
        await server.HandleAuthenticatedConnectionAsync(
            seedChannel,
            seedLifetime,
            CreateRegistration(101, restartRequired: true, recoverLock: true, null),
            CancellationToken.None);
        Assert.IsTrue(source.Current.RestartRequired);
        return new TestContext(source, registry, server);
    }

    private static LatestSupervisionCommandSource CreateReleasedSource() =>
        new(new SupervisionDirective(
            Revision: 0,
            RestartRequired: false,
            SupervisionDirectiveReason.ServiceStarted));

    private static LatestSupervisionCommandSource CreateInitialLaunchSource() =>
        new(WindowsServiceHost.CreateInitialSupervisionDirective());

    private static WindowsSupervisionPipeServer CreateServer(
        LatestSupervisionCommandSource source,
        DesktopLaunchHandshakeRegistry registry,
        bool requireInitialServiceLaunch = false) =>
        new(
            source,
            registry,
            SessionId,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            requireInitialServiceLaunch: requireInitialServiceLaunch);

    private static SupervisionRequest CreateRegistration(
        int processId,
        bool restartRequired,
        bool recoverLock,
        string? launchToken) =>
        new(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.Register,
            new SupervisedProcessIdentity(
                processId,
                ProcessStartTimeUtcTicks: 1,
                SessionId,
                "S-1-5-32-545"),
            new SupervisionLease(0, restartRequired, recoverLock),
            Revision: null,
            launchToken);

    private static SupervisionRequest CreateHealthyRequest(long revision) =>
        new(
            SupervisionProtocol.CurrentVersion,
            SupervisionRequestKind.ReportHealthy,
            Process: null,
            Lease: null,
            revision,
            LaunchToken: null);

    private static SupervisionResponse AssertSingleResponse(ScriptedChannel channel)
    {
        SupervisionResponse[] responses = channel.Responses.ToArray();
        Assert.HasCount(1, responses);
        return responses[0];
    }

    private sealed record TestContext(
        LatestSupervisionCommandSource Source,
        DesktopLaunchHandshakeRegistry Registry,
        WindowsSupervisionPipeServer Server);

    private sealed class ScriptedChannel : ISupervisionMessageChannel
    {
        private readonly ConcurrentQueue<SupervisionRequest> _requests = new();
        private readonly TaskCompletionSource<SupervisionRequest?> _readCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _failWriteNumber;
        private readonly bool _completeReadsWhenEmpty;
        private int _writeCount;

        public ScriptedChannel(
            IEnumerable<SupervisionRequest>? requests = null,
            int failWriteNumber = 0,
            bool completeReadsWhenEmpty = true)
        {
            if (requests is not null)
            {
                foreach (SupervisionRequest request in requests)
                {
                    _requests.Enqueue(request);
                }
            }

            _failWriteNumber = failWriteNumber;
            _completeReadsWhenEmpty = completeReadsWhenEmpty;
        }

        public ConcurrentQueue<SupervisionResponse> Responses { get; } = new();

        public TaskCompletionSource FirstWrite { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SupervisionRequest?> ReadAsync(CancellationToken cancellationToken)
        {
            if (_requests.TryDequeue(out SupervisionRequest? request))
            {
                return ValueTask.FromResult<SupervisionRequest?>(request);
            }

            return _completeReadsWhenEmpty
                ? ValueTask.FromResult<SupervisionRequest?>(null)
                : new ValueTask<SupervisionRequest?>(
                    _readCompletion.Task.WaitAsync(cancellationToken));
        }

        public ValueTask WriteAsync(
            SupervisionResponse response,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int writeNumber = Interlocked.Increment(ref _writeCount);
            FirstWrite.TrySetResult();
            if (writeNumber == _failWriteNumber)
            {
                throw new IOException("Injected response write failure.");
            }

            Responses.Enqueue(response);
            return ValueTask.CompletedTask;
        }

        public void CompleteReads()
        {
            _readCompletion.TrySetResult(null);
        }
    }

    private sealed class FakeDesktopLifetime : IAuthenticatedDesktopLifetime
    {
        private readonly TaskCompletionSource _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exitCode;
        private int _waitCount;

        public FakeDesktopLifetime(int processId)
        {
            ProcessId = processId;
        }

        public int ProcessId { get; }

        public int ExitCode => _exitCode;

        public int WaitCount => Volatile.Read(ref _waitCount);

        public void Exit(int exitCode)
        {
            _exitCode = exitCode;
            _exited.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _waitCount);
            return _exited.Task.WaitAsync(cancellationToken);
        }
    }
}
