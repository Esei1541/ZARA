using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zara.Application.Continuity;
using Zara.Core.Continuity;
using Zara.Core.Runtime;
using Zara.Infrastructure.Windows.Continuity;
using Zara.Supervision.Contracts;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsSupervisionConnectionTests
{
    private static readonly Encoding PipeEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [TestMethod]
    public async Task RegisterAtomicallySendsProcessIdentityAndInitialLease()
    {
        string pipeName = CreatePipeName();
        const string launchToken = "service-recovery-token";
        var initialLease = new SupervisionLease(
            Revision: 0,
            RestartRequiredAfterExit: true,
            RecoverLockOnRestart: false);
        SupervisionRequest? observedRequest = null;
        Task server = RunServerAsync(
            pipeName,
            async (reader, writer) =>
            {
                observedRequest = await WindowsSupervisionConnection
                    .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
                await WriteResponseAsync(
                    writer,
                    SupervisionResponseKind.Registered,
                    acknowledgedRevision: 27,
                    recoverLockOnStart: true);
            });

        await using WindowsSupervisionConnection connection =
            await WindowsSupervisionConnection.ConnectAsync(
                pipeName,
                launchToken,
                initialLease,
                AcceptingServerVerifier.Instance,
                CancellationToken.None);
        await server;

        SupervisionRequest request = observedRequest ??
            throw new AssertFailedException("The fake Service did not observe registration.");
        Assert.AreEqual(SupervisionProtocol.CurrentVersion, request.ProtocolVersion);
        Assert.AreEqual(SupervisionRequestKind.Register, request.Kind);
        Assert.AreEqual(initialLease, request.Lease);
        Assert.IsNull(request.Revision);
        Assert.AreEqual(launchToken, request.LaunchToken);
        Assert.IsNotNull(request.Process);
        using Process currentProcess = Process.GetCurrentProcess();
        using WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent();
        Assert.AreEqual(currentProcess.Id, request.Process.ProcessId);
        Assert.AreEqual(
            currentProcess.StartTime.ToUniversalTime().Ticks,
            request.Process.ProcessStartTimeUtcTicks);
        Assert.AreEqual(currentProcess.SessionId, request.Process.SessionId);
        Assert.AreEqual(currentIdentity.User?.Value, request.Process.UserSid);
        Assert.AreEqual(27, connection.Registration.AcknowledgedRevision);
        Assert.IsTrue(connection.Registration.RecoverLockOnStart);
    }

    [TestMethod]
    public async Task ServerVerificationFailureClosesPipeBeforeRegistrationIsWritten()
    {
        string pipeName = CreatePipeName();
        int verificationCount = 0;
        Task<int> server = ReadOneByteFromClientAsync(pipeName);
        var verifier = new DelegateServerVerifier(_ =>
        {
            Interlocked.Increment(ref verificationCount);
            throw new InvalidDataException("The fake pipe server is not trusted.");
        });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            WindowsSupervisionConnection.ConnectAsync(
                pipeName,
                launchToken: null,
                CreateInitialLease(),
                verifier,
                CancellationToken.None));
        int observedByteCount = await server;

        Assert.AreEqual(1, verificationCount);
        Assert.AreEqual(0, observedByteCount);
    }

    [TestMethod]
    public void ServerIdentityValidationAcceptsOnlyTheExactServiceIdentity()
    {
        string expectedPath = Path.Combine(
            Path.GetTempPath(),
            "ZARA",
            WindowsSupervisionServerVerifier.ServiceExecutableName);
        SupervisionServerIdentity valid = CreateValidServerIdentity($"\"{expectedPath}\"");

        WindowsSupervisionServerVerifier.ValidateIdentity(valid, expectedPath);

        SupervisionServerIdentity[] invalidIdentities =
        [
            valid with { ServiceProcessId = valid.PipeServerProcessId + 1 },
            valid with { ServiceState = 1 },
            valid with { ServiceAccountName = "NT AUTHORITY\\LocalService" },
            valid with
            {
                ServiceBinaryPath = Path.Combine(
                    Path.GetDirectoryName(expectedPath)!,
                    "Impostor.Service.exe"),
            },
            valid with { ServiceBinaryPath = $"\"{expectedPath}\" --unexpected" },
            valid with { ServiceBinaryPath = $"\"{expectedPath}\" \"--unexpected\"" },
        ];

        foreach (SupervisionServerIdentity identity in invalidIdentities)
        {
            Assert.ThrowsExactly<InvalidDataException>(
                () => WindowsSupervisionServerVerifier.ValidateIdentity(identity, expectedPath));
        }
    }

    [TestMethod]
    public void ServerIdentityValidationRejectsNonDedicatedServiceTypesFromStatusOrConfiguration()
    {
        string expectedPath = Path.Combine(
            Path.GetTempPath(),
            "ZARA",
            WindowsSupervisionServerVerifier.ServiceExecutableName);
        SupervisionServerIdentity valid = CreateValidServerIdentity(expectedPath);

        (uint StatusType, uint ConfiguredType)[] invalidServiceTypes =
        [
            (0x00000020, 0x00000010),
            (0x00000010, 0x00000020),
            (0x00000040, 0x00000010),
            (0x00000010, 0x00000040),
            (0x00000110, 0x00000010),
            (0x00000010, 0x00000110),
            (0x00000020, 0x00000110),
        ];

        foreach ((uint statusType, uint configuredType) in invalidServiceTypes)
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                WindowsSupervisionServerVerifier.ValidateIdentity(
                    valid with
                    {
                        ServiceType = statusType,
                        ConfiguredServiceType = configuredType,
                    },
                    expectedPath));
        }
    }

    [TestMethod]
    public void ServerVerificationRequestsOnlyStandardUserServiceQueryRights()
    {
        const uint serviceQueryConfig = 0x0001;
        const uint serviceQueryStatus = 0x0004;

        Assert.AreEqual(
            serviceQueryConfig | serviceQueryStatus,
            WindowsSupervisionServerVerifier.RequiredServiceAccess);
    }

    [TestMethod]
    public async Task PublishMapsApplicationDecisionToSharedLeaseAndReturnsExactAck()
    {
        string pipeName = CreatePipeName();
        SupervisionRequest? observedUpdate = null;
        Task server = RunServerAsync(
            pipeName,
            async (reader, writer) =>
            {
                await AcceptRegistrationAsync(reader, writer);
                observedUpdate = await WindowsSupervisionConnection
                    .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
                await WriteResponseAsync(
                    writer,
                    SupervisionResponseKind.LeaseAcknowledged,
                    acknowledgedRevision: 4);
            });
        await using WindowsSupervisionConnection connection = await ConnectAsync(pipeName);
        var lease = new RestartContinuityLease(
            Revision: 4,
            LockRequired: true,
            RestartWhenAvailable: false,
            OverlayProjection: OverlayProjectionState.Unknown,
            Decision: new RestartContinuityDecision(
                RestartRequired: true,
                RecoverLock: true));

        RestartContinuityAcknowledgement acknowledgement =
            await connection.PublishAsync(lease, CancellationToken.None);
        await server;

        Assert.AreEqual(lease.Revision, acknowledgement.Revision);
        SupervisionRequest request = observedUpdate ??
            throw new AssertFailedException("The fake Service did not observe a lease update.");
        Assert.AreEqual(SupervisionRequestKind.UpdateLease, request.Kind);
        Assert.IsNull(request.Process);
        Assert.AreEqual(
            new SupervisionLease(
                lease.Revision,
                RestartRequiredAfterExit: true,
                RecoverLockOnRestart: true),
            request.Lease);
        Assert.IsNull(request.Revision);
        Assert.IsNull(request.LaunchToken);
    }

    [TestMethod]
    public async Task PreparedLeaseSendsOrderedRevisionOnlyCommitWithoutWaitingForSecondAck()
    {
        string pipeName = CreatePipeName();
        SupervisionRequest? observedUpdate = null;
        SupervisionRequest? observedCommit = null;
        Task server = RunServerAsync(
            pipeName,
            async (reader, writer) =>
            {
                await AcceptRegistrationAsync(reader, writer);
                observedUpdate = await WindowsSupervisionConnection
                    .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
                await WriteResponseAsync(
                    writer,
                    SupervisionResponseKind.LeasePrepared,
                    acknowledgedRevision: 4);
                observedCommit = await WindowsSupervisionConnection
                    .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
            });
        await using WindowsSupervisionConnection connection = await ConnectAsync(pipeName);
        var lease = new RestartContinuityLease(
            Revision: 4,
            LockRequired: false,
            RestartWhenAvailable: false,
            OverlayProjection: OverlayProjectionState.Hidden,
            Decision: new RestartContinuityDecision(
                RestartRequired: false,
                RecoverLock: false));

        RestartContinuityAcknowledgement acknowledgement =
            await connection.PublishAsync(lease, CancellationToken.None);
        await server;

        Assert.AreEqual(4, acknowledgement.Revision);
        Assert.AreEqual(SupervisionRequestKind.UpdateLease, observedUpdate?.Kind);
        Assert.AreEqual(SupervisionRequestKind.CommitLease, observedCommit?.Kind);
        Assert.IsNull(observedCommit?.Process);
        Assert.IsNull(observedCommit?.Lease);
        Assert.AreEqual(4, observedCommit?.Revision);
        Assert.IsNull(observedCommit?.LaunchToken);
    }

    [TestMethod]
    public async Task HealthyUsesRevisionOnlyAndReleaseUsesClearedSharedLease()
    {
        string pipeName = CreatePipeName();
        SupervisionRequest? observedHealthy = null;
        SupervisionRequest? observedRelease = null;
        Task server = RunServerAsync(
            pipeName,
            async (reader, writer) =>
            {
                await AcceptRegistrationAsync(reader, writer);
                observedHealthy = await WindowsSupervisionConnection
                    .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
                await WriteResponseAsync(
                    writer,
                    SupervisionResponseKind.HealthyAcknowledged,
                    acknowledgedRevision: 0);
                observedRelease = await WindowsSupervisionConnection
                    .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
                await WriteResponseAsync(
                    writer,
                    SupervisionResponseKind.ExitAcknowledged,
                    acknowledgedRevision: 1);
            });
        await using WindowsSupervisionConnection connection = await ConnectAsync(pipeName);

        await connection.ReportHealthyAsync(revision: 0);
        RestartContinuityAcknowledgement acknowledgement = await connection.ReleaseAsync(
            new RestartContinuityRelease(Revision: 1),
            CancellationToken.None);
        await server;

        SupervisionRequest healthy = observedHealthy ??
            throw new AssertFailedException("The fake Service did not observe a health report.");
        Assert.AreEqual(SupervisionRequestKind.ReportHealthy, healthy.Kind);
        Assert.IsNull(healthy.Process);
        Assert.IsNull(healthy.Lease);
        Assert.AreEqual(0, healthy.Revision);
        Assert.IsNull(healthy.LaunchToken);

        SupervisionRequest release = observedRelease ??
            throw new AssertFailedException("The fake Service did not observe an explicit release.");
        Assert.AreEqual(SupervisionRequestKind.ReleaseForExplicitExit, release.Kind);
        Assert.IsNull(release.Process);
        Assert.AreEqual(
            new SupervisionLease(
                Revision: 1,
                RestartRequiredAfterExit: false,
                RecoverLockOnRestart: false),
            release.Lease);
        Assert.IsNull(release.Revision);
        Assert.IsNull(release.LaunchToken);
        Assert.AreEqual(1, acknowledgement.Revision);
    }

    [TestMethod]
    public async Task PublishRejectsAcknowledgementForDifferentRevision()
    {
        string pipeName = CreatePipeName();
        Task server = RunServerAsync(
            pipeName,
            async (reader, writer) =>
            {
                await AcceptRegistrationAsync(reader, writer);
                _ = await WindowsSupervisionConnection
                    .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
                await WriteResponseAsync(
                    writer,
                    SupervisionResponseKind.LeaseAcknowledged,
                    acknowledgedRevision: 6);
            });
        await using WindowsSupervisionConnection connection = await ConnectAsync(pipeName);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => connection.PublishAsync(CreateApplicationLease(revision: 5), CancellationToken.None));
        await server;
    }

    [TestMethod]
    public async Task PublishRejectsAcknowledgementWithWrongKind()
    {
        string pipeName = CreatePipeName();
        Task server = RunServerAsync(
            pipeName,
            async (reader, writer) =>
            {
                await AcceptRegistrationAsync(reader, writer);
                _ = await WindowsSupervisionConnection
                    .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
                await WriteResponseAsync(
                    writer,
                    SupervisionResponseKind.HealthyAcknowledged,
                    acknowledgedRevision: 5);
            });
        await using WindowsSupervisionConnection connection = await ConnectAsync(pipeName);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => connection.PublishAsync(CreateApplicationLease(revision: 5), CancellationToken.None));
        await server;
    }

    [TestMethod]
    public async Task ConcurrentPublishesUseOneOrderedExchangeGate()
    {
        string pipeName = CreatePipeName();
        var observedRevisions = new List<long>();
        Task server = RunServerAsync(
            pipeName,
            async (reader, writer) =>
            {
                await AcceptRegistrationAsync(reader, writer);
                for (int index = 0; index < 2; index++)
                {
                    SupervisionRequest request = await WindowsSupervisionConnection
                        .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
                    long revision = request.Lease?.Revision ??
                        throw new AssertFailedException("A publish did not contain its lease.");
                    observedRevisions.Add(revision);
                    await WriteResponseAsync(
                        writer,
                        SupervisionResponseKind.LeaseAcknowledged,
                        revision);
                }
            });
        await using WindowsSupervisionConnection connection = await ConnectAsync(pipeName);

        Task<RestartContinuityAcknowledgement> first = connection.PublishAsync(
            CreateApplicationLease(revision: 1),
            CancellationToken.None);
        Task<RestartContinuityAcknowledgement> second = connection.PublishAsync(
            CreateApplicationLease(revision: 2),
            CancellationToken.None);
        RestartContinuityAcknowledgement[] acknowledgements = await Task.WhenAll(first, second);
        await server;

        CollectionAssert.AreEquivalent(new long[] { 1, 2 }, observedRevisions);
        CollectionAssert.AreEquivalent(
            new long[] { 1, 2 },
            acknowledgements.Select(acknowledgement => acknowledgement.Revision).ToArray());
    }

    [TestMethod]
    public async Task BoundedReaderRejectsOversizedLine()
    {
        string oversizedLine =
            new string('x', SupervisionProtocol.MaximumMessageCharacters + 1) + "\n";
        await using var stream = new MemoryStream(PipeEncoding.GetBytes(oversizedLine));
        using var reader = new StreamReader(stream, PipeEncoding, leaveOpen: true);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => WindowsSupervisionConnection.ReadMessageAsync<SupervisionResponse>(
                reader,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task BoundedReaderRejectsEndOfStreamBeforeLineCompletes()
    {
        await using var stream = new MemoryStream(PipeEncoding.GetBytes("{}"));
        using var reader = new StreamReader(stream, PipeEncoding, leaveOpen: true);

        await Assert.ThrowsExactlyAsync<EndOfStreamException>(
            () => WindowsSupervisionConnection.ReadMessageAsync<SupervisionResponse>(
                reader,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task DisposalIsIdempotentAfterServiceClosesPipe()
    {
        string pipeName = CreatePipeName();
        Task server = RunServerAsync(
            pipeName,
            async (reader, writer) =>
            {
                await AcceptRegistrationAsync(reader, writer);
            });
        WindowsSupervisionConnection connection = await ConnectAsync(pipeName);
        await server;

        await connection.DisposeAsync();
        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task TaskManagerRequestsHaveNoCallerSelectedUserOrRegistryPayload()
    {
        string pipeName = CreatePipeName();
        Task server = RunServerAsync(pipeName, async (reader, writer) =>
        {
            _ = await AcceptRegistrationAsync(reader, writer);
            foreach (var expected in new[]
            {
                (SupervisionRequestKind.RestrictTaskManager, SupervisionResponseKind.TaskManagerRestricted),
                (SupervisionRequestKind.RestoreTaskManager, SupervisionResponseKind.TaskManagerRestored),
            })
            {
                SupervisionRequest request = await WindowsSupervisionConnection
                    .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
                Assert.AreEqual(expected.Item1, request.Kind);
                Assert.IsNull(request.Process);
                Assert.IsNull(request.Lease);
                Assert.IsNull(request.Revision);
                Assert.IsNull(request.LaunchToken);
                await WriteResponseAsync(writer, expected.Item2, acknowledgedRevision: 0);
            }
        });

        await using WindowsSupervisionConnection connection = await ConnectAsync(pipeName);
        await connection.EnableAsync(CancellationToken.None);
        await connection.DisableAsync(CancellationToken.None);
        await server;
    }

    [TestMethod]
    public async Task TaskManagerFailureDoesNotConsumeFollowingLeaseAcknowledgement()
    {
        string pipeName = CreatePipeName();
        Task server = RunServerAsync(pipeName, async (reader, writer) =>
        {
            _ = await AcceptRegistrationAsync(reader, writer);
            _ = await WindowsSupervisionConnection.ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
            await WindowsSupervisionConnection.WriteMessageAsync(writer,
                new SupervisionResponse(SupervisionProtocol.CurrentVersion,
                    SupervisionResponseKind.Rejected, 0, false, "TASK_MANAGER_POLICY_FAILED"),
                CancellationToken.None);
            SupervisionRequest next = await WindowsSupervisionConnection
                .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
            Assert.AreEqual(SupervisionRequestKind.UpdateLease, next.Kind);
            await WriteResponseAsync(writer, SupervisionResponseKind.LeaseAcknowledged, acknowledgedRevision: 1);
        });

        await using WindowsSupervisionConnection connection = await ConnectAsync(pipeName);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => connection.EnableAsync(CancellationToken.None));
        _ = await connection.PublishAsync(CreateApplicationLease(1), CancellationToken.None);
        await server;
    }

    private static RestartContinuityLease CreateApplicationLease(long revision) =>
        new(
            revision,
            LockRequired: false,
            RestartWhenAvailable: true,
            OverlayProjection: OverlayProjectionState.Hidden,
            Decision: new RestartContinuityDecision(
                RestartRequired: true,
                RecoverLock: false));

    [TestMethod]
    [DataRow("SERVICE_LAUNCH_TOKEN_REQUIRED", true)]
    [DataRow("RECOVERY_TOKEN_REQUIRED", true)]
    [DataRow("INVALID_LAUNCH_TOKEN", false)]
    public async Task RegistrationRejectionRetainsServiceOwnedLaunchRequirement(string code, bool waitForService)
    {
        string pipeName = CreatePipeName();
        Task server = RunServerAsync(pipeName, async (reader, writer) =>
        {
            _ = await WindowsSupervisionConnection.ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
            await WindowsSupervisionConnection.WriteMessageAsync(writer,
                new SupervisionResponse(SupervisionProtocol.CurrentVersion, SupervisionResponseKind.Rejected, 0, false, code),
                CancellationToken.None);
        });
        SupervisionRegistrationException exception = await Assert.ThrowsExactlyAsync<SupervisionRegistrationException>(
            () => ConnectAsync(pipeName));
        await server;
        Assert.AreEqual(code, exception.ErrorCode);
        Assert.AreEqual(waitForService, exception.RequiresServiceDesktop);
    }
    private static SupervisionLease CreateInitialLease() =>
        new(
            Revision: 0,
            RestartRequiredAfterExit: true,
            RecoverLockOnRestart: false);

    private static SupervisionServerIdentity CreateValidServerIdentity(string serviceBinaryPath) =>
        new(
            PipeServerProcessId: 41,
            ServiceProcessId: 41,
            ServiceState: 4,
            ServiceType: 0x00000010,
            ConfiguredServiceType: 0x00000010,
            ServiceAccountName: "LocalSystem",
            ServiceBinaryPath: serviceBinaryPath);

    private static Task<WindowsSupervisionConnection> ConnectAsync(string pipeName) =>
        WindowsSupervisionConnection.ConnectAsync(
            pipeName,
            launchToken: null,
            CreateInitialLease(),
            AcceptingServerVerifier.Instance,
            CancellationToken.None);

    private static async Task<SupervisionRequest> AcceptRegistrationAsync(
        StreamReader reader,
        StreamWriter writer)
    {
        SupervisionRequest request = await WindowsSupervisionConnection
            .ReadMessageAsync<SupervisionRequest>(reader, CancellationToken.None);
        await WriteResponseAsync(
            writer,
            SupervisionResponseKind.Registered,
            acknowledgedRevision: 0);
        return request;
    }

    private static Task WriteResponseAsync(
        StreamWriter writer,
        SupervisionResponseKind kind,
        long acknowledgedRevision,
        bool recoverLockOnStart = false) =>
        WindowsSupervisionConnection.WriteMessageAsync(
            writer,
            new SupervisionResponse(
                SupervisionProtocol.CurrentVersion,
                kind,
                acknowledgedRevision,
                recoverLockOnStart,
                ErrorCode: null),
            CancellationToken.None);

    private static async Task RunServerAsync(
        string pipeName,
        Func<StreamReader, StreamWriter, Task> conversation)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(
            server,
            PipeEncoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            server,
            PipeEncoding,
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        await conversation(reader, writer);
    }

    private static async Task<int> ReadOneByteFromClientAsync(string pipeName)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync();
        var buffer = new byte[1];
        try
        {
            return await server.ReadAsync(buffer.AsMemory(), CancellationToken.None);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string CreatePipeName() =>
        $"ZARA.Supervision.Tests.{Guid.NewGuid():N}";

    private sealed class AcceptingServerVerifier : ISupervisionServerVerifier
    {
        internal static AcceptingServerVerifier Instance { get; } = new();

        public void Verify(SafePipeHandle pipeHandle)
        {
            Assert.IsFalse(pipeHandle.IsInvalid);
            Assert.IsFalse(pipeHandle.IsClosed);
        }
    }

    private sealed class DelegateServerVerifier(
        Action<SafePipeHandle> verify) : ISupervisionServerVerifier
    {
        public void Verify(SafePipeHandle pipeHandle)
        {
            verify(pipeHandle);
        }
    }
}
