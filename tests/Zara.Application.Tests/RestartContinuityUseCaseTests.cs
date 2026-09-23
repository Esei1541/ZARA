using System.Collections.Concurrent;
using Zara.Application.Continuity;
using Zara.Core.Runtime;

namespace Zara.Application.Tests;

[TestClass]
public sealed class RestartContinuityUseCaseTests
{
    [TestMethod]
    public void ConstructorWithNullPortThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new RestartContinuityUseCase(null!));
    }

    [TestMethod]
    public async Task MissingRestartSettingPublishesEnabledDefault()
    {
        var port = new RecordingContinuityPort();
        using var useCase = new RestartContinuityUseCase(port);

        RestartContinuityLease lease =
            await useCase.PublishLockConditionAsync(lockRequired: false);

        Assert.AreEqual(1, lease.Revision);
        Assert.IsFalse(lease.LockRequired);
        Assert.IsTrue(lease.RestartWhenAvailable);
        Assert.AreEqual(OverlayProjectionState.Hidden, lease.OverlayProjection);
        Assert.IsTrue(lease.Decision.RestartRequired);
        Assert.IsFalse(lease.Decision.RecoverLock);
        Assert.AreSame(lease, useCase.CurrentAcknowledgedLease);
    }

    [TestMethod]
    public async Task DisabledRestartSettingDoesNotRestartWhenLockIsNotRequired()
    {
        var port = new RecordingContinuityPort();
        using var useCase = new RestartContinuityUseCase(
            port,
            restartWhenAvailable: false);

        RestartContinuityLease lease =
            await useCase.PublishLockConditionAsync(lockRequired: false);

        Assert.IsFalse(lease.RestartWhenAvailable);
        Assert.IsFalse(lease.Decision.RestartRequired);
        Assert.IsFalse(lease.Decision.RecoverLock);
    }

    [TestMethod]
    public async Task RequiredLockOverridesDisabledRestartSetting()
    {
        var port = new RecordingContinuityPort();
        using var useCase = new RestartContinuityUseCase(
            port,
            restartWhenAvailable: false);

        RestartContinuityLease lease =
            await useCase.PublishLockConditionAsync(lockRequired: true);

        Assert.IsTrue(lease.LockRequired);
        Assert.IsFalse(lease.RestartWhenAvailable);
        Assert.IsTrue(lease.Decision.RestartRequired);
        Assert.IsTrue(lease.Decision.RecoverLock);
    }

    [TestMethod]
    public async Task OverlayProjectionChangesWithoutChangingRequiredLock()
    {
        var port = new RecordingContinuityPort();
        using var useCase = new RestartContinuityUseCase(
            port,
            restartWhenAvailable: false);
        _ = await useCase.PublishLockConditionAsync(lockRequired: true);

        RestartContinuityLease lease = await useCase.PublishOverlayProjectionAsync(
            OverlayProjectionState.Hidden);

        Assert.IsTrue(lease.LockRequired);
        Assert.AreEqual(OverlayProjectionState.Hidden, lease.OverlayProjection);
        Assert.IsTrue(lease.Decision.RestartRequired);
        Assert.IsTrue(lease.Decision.RecoverLock);
    }

    [TestMethod]
    public async Task LockConditionChangesWithoutChangingVisibleProjection()
    {
        var port = new RecordingContinuityPort();
        using var useCase = new RestartContinuityUseCase(
            port,
            restartWhenAvailable: false);
        _ = await useCase.PublishOverlayProjectionAsync(OverlayProjectionState.Visible);

        RestartContinuityLease lease =
            await useCase.PublishLockConditionAsync(lockRequired: false);

        Assert.IsFalse(lease.LockRequired);
        Assert.AreEqual(OverlayProjectionState.Visible, lease.OverlayProjection);
        Assert.IsFalse(lease.Decision.RestartRequired);
        Assert.IsFalse(lease.Decision.RecoverLock);
    }

    [TestMethod]
    public async Task MissingUpdatedSettingRestoresEnabledDefault()
    {
        var port = new RecordingContinuityPort();
        using var useCase = new RestartContinuityUseCase(
            port,
            restartWhenAvailable: false);
        _ = await useCase.PublishLockConditionAsync(lockRequired: false);

        RestartContinuityLease lease =
            await useCase.PublishRestartWhenAvailableAsync(restartWhenAvailable: null);

        Assert.IsTrue(lease.RestartWhenAvailable);
        Assert.IsTrue(lease.Decision.RestartRequired);
        Assert.IsFalse(lease.Decision.RecoverLock);
    }

    [TestMethod]
    public async Task SuccessfulPublishesUseMonotonicallyIncreasingRevisions()
    {
        var port = new RecordingContinuityPort();
        using var useCase = new RestartContinuityUseCase(port);

        RestartContinuityLease first =
            await useCase.PublishLockConditionAsync(lockRequired: true);
        RestartContinuityLease second = await useCase.PublishOverlayProjectionAsync(
            OverlayProjectionState.Visible);
        RestartContinuityLease third =
            await useCase.PublishRestartWhenAvailableAsync(restartWhenAvailable: false);

        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3 },
            port.PublishedLeases.Select(lease => lease.Revision).ToArray());
        Assert.AreEqual(1, first.Revision);
        Assert.AreEqual(2, second.Revision);
        Assert.AreEqual(3, third.Revision);
    }

    [TestMethod]
    public async Task FailedPublishConsumesRevisionWithoutReplacingAcknowledgedState()
    {
        int callCount = 0;
        var port = new RecordingContinuityPort
        {
            PublishHandler = (lease, _) =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromException<RestartContinuityAcknowledgement>(
                        new IOException("transport failed"))
                    : Task.FromResult(new RestartContinuityAcknowledgement(lease.Revision));
            },
        };
        using var useCase = new RestartContinuityUseCase(port);

        _ = await Assert.ThrowsExactlyAsync<IOException>(
            () => useCase.PublishLockConditionAsync(lockRequired: true));
        Assert.IsNull(useCase.CurrentAcknowledgedLease);

        RestartContinuityLease lease = await useCase.PublishOverlayProjectionAsync(
            OverlayProjectionState.Visible);

        Assert.AreEqual(2, lease.Revision);
        Assert.IsTrue(lease.LockRequired);
        Assert.AreEqual(OverlayProjectionState.Visible, lease.OverlayProjection);
        Assert.AreSame(lease, useCase.CurrentAcknowledgedLease);
    }

    [TestMethod]
    public async Task MismatchedAcknowledgementDoesNotReplaceAcknowledgedState()
    {
        int callCount = 0;
        var port = new RecordingContinuityPort
        {
            PublishHandler = (lease, _) =>
            {
                callCount++;
                long acknowledgedRevision = callCount == 1
                    ? lease.Revision + 1
                    : lease.Revision;
                return Task.FromResult(
                    new RestartContinuityAcknowledgement(acknowledgedRevision));
            },
        };
        using var useCase = new RestartContinuityUseCase(port);

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.PublishLockConditionAsync(lockRequired: true));
        Assert.IsNull(useCase.CurrentAcknowledgedLease);

        RestartContinuityLease lease = await useCase.PublishOverlayProjectionAsync(
            OverlayProjectionState.Visible);

        Assert.AreEqual(2, lease.Revision);
        Assert.IsTrue(lease.LockRequired);
        Assert.AreEqual(OverlayProjectionState.Visible, lease.OverlayProjection);
    }

    [TestMethod]
    public async Task ConcurrentPublishesWaitForThePreviousAcknowledgement()
    {
        var firstPublishReached =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgeFirst =
            new TaskCompletionSource<RestartContinuityAcknowledgement>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new RecordingContinuityPort
        {
            PublishHandler = (lease, _) =>
            {
                if (lease.Revision == 1)
                {
                    firstPublishReached.TrySetResult();
                    return acknowledgeFirst.Task;
                }

                return Task.FromResult(
                    new RestartContinuityAcknowledgement(lease.Revision));
            },
        };
        using var useCase = new RestartContinuityUseCase(port);

        Task<RestartContinuityLease> first =
            useCase.PublishLockConditionAsync(lockRequired: true);
        await firstPublishReached.Task;
        Task<RestartContinuityLease> second = useCase.PublishOverlayProjectionAsync(
            OverlayProjectionState.Visible);
        await Task.Yield();

        Assert.HasCount(1, port.PublishedLeases);
        Assert.IsNull(useCase.CurrentAcknowledgedLease);

        acknowledgeFirst.SetResult(new RestartContinuityAcknowledgement(Revision: 1));
        RestartContinuityLease[] results = await Task.WhenAll(first, second);

        Assert.HasCount(2, port.PublishedLeases);
        Assert.AreEqual(1, results[0].Revision);
        Assert.AreEqual(2, results[1].Revision);
        Assert.IsTrue(results[1].LockRequired);
        Assert.AreEqual(OverlayProjectionState.Visible, results[1].OverlayProjection);
    }

    [TestMethod]
    public async Task ExplicitExitCannotReleaseRequiredLockRecovery()
    {
        var port = new RecordingContinuityPort();
        using var useCase = new RestartContinuityUseCase(port);
        _ = await useCase.PublishLockConditionAsync(lockRequired: true);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.ReleaseForExplicitExitAsync());

        Assert.HasCount(1, port.PublishedLeases);
        Assert.IsTrue(port.PublishedLeases[0].LockRequired);
        Assert.IsEmpty(port.Releases);
        Assert.IsFalse(useCase.IsReleased);
        Assert.IsTrue(useCase.CurrentAcknowledgedLease!.Decision.RecoverLock);

        await useCase.PublishLockConditionAsync(lockRequired: false);
        RestartContinuityRelease release = await useCase.ReleaseForExplicitExitAsync();
        Assert.AreEqual(3, release.Revision);
        Assert.IsTrue(useCase.IsReleased);
    }

    [TestMethod]
    public async Task AcknowledgedExplicitExitIsIdempotentAndTerminal()
    {
        var port = new RecordingContinuityPort();
        using var useCase = new RestartContinuityUseCase(port);

        RestartContinuityRelease first =
            await useCase.ReleaseForExplicitExitAsync();
        RestartContinuityRelease second =
            await useCase.ReleaseForExplicitExitAsync();

        Assert.AreSame(first, second);
        Assert.HasCount(1, port.Releases);
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => useCase.PublishLockConditionAsync(lockRequired: true));
        Assert.IsEmpty(port.PublishedLeases);
    }

    [TestMethod]
    public async Task FailedExplicitExitCanRetryWithANewerRevision()
    {
        int callCount = 0;
        var port = new RecordingContinuityPort
        {
            ReleaseHandler = (release, _) =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromException<RestartContinuityAcknowledgement>(
                        new IOException("transport failed"))
                    : Task.FromResult(
                        new RestartContinuityAcknowledgement(release.Revision));
            },
        };
        using var useCase = new RestartContinuityUseCase(port);

        _ = await Assert.ThrowsExactlyAsync<IOException>(
            () => useCase.ReleaseForExplicitExitAsync());
        Assert.IsFalse(useCase.IsReleased);

        RestartContinuityRelease release =
            await useCase.ReleaseForExplicitExitAsync();

        Assert.AreEqual(2, release.Revision);
        Assert.IsTrue(useCase.IsReleased);
    }

    [TestMethod]
    public async Task InvalidOverlayProjectionIsRejectedBeforePublishing()
    {
        var port = new RecordingContinuityPort();
        using var useCase = new RestartContinuityUseCase(port);

        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => useCase.PublishOverlayProjectionAsync((OverlayProjectionState)int.MaxValue));

        RestartContinuityLease lease = await useCase.PublishOverlayProjectionAsync(
            OverlayProjectionState.Unknown);
        Assert.AreEqual(1, lease.Revision);
        Assert.HasCount(1, port.PublishedLeases);
    }

    [TestMethod]
    public async Task OperationsAfterDisposeThrowObjectDisposedException()
    {
        var port = new RecordingContinuityPort();
        var useCase = new RestartContinuityUseCase(port);
        useCase.Dispose();

        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => useCase.PublishLockConditionAsync(lockRequired: true));
        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => useCase.ReleaseForExplicitExitAsync());
    }

    private sealed class RecordingContinuityPort : IRestartContinuityPort
    {
        private readonly ConcurrentQueue<RestartContinuityLease> _publishedLeases = new();
        private readonly ConcurrentQueue<RestartContinuityRelease> _releases = new();

        public Func<
            RestartContinuityLease,
            CancellationToken,
            Task<RestartContinuityAcknowledgement>>?
            PublishHandler
        {
            get;
            init;
        }

        public Func<
            RestartContinuityRelease,
            CancellationToken,
            Task<RestartContinuityAcknowledgement>>?
            ReleaseHandler
        {
            get;
            init;
        }

        public RestartContinuityLease[] PublishedLeases => _publishedLeases.ToArray();

        public RestartContinuityRelease[] Releases => _releases.ToArray();

        public Task<RestartContinuityAcknowledgement> PublishAsync(
            RestartContinuityLease lease,
            CancellationToken cancellationToken)
        {
            _publishedLeases.Enqueue(lease);
            return PublishHandler?.Invoke(lease, cancellationToken) ??
                Task.FromResult(new RestartContinuityAcknowledgement(lease.Revision));
        }

        public Task<RestartContinuityAcknowledgement> ReleaseAsync(
            RestartContinuityRelease release,
            CancellationToken cancellationToken)
        {
            _releases.Enqueue(release);
            return ReleaseHandler?.Invoke(release, cancellationToken) ??
                Task.FromResult(new RestartContinuityAcknowledgement(release.Revision));
        }
    }
}
