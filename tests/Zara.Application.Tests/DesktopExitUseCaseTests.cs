using Zara.Application.Continuity;
using Zara.Application.Locking;
using Zara.Application.UsagePolicy;
using Zara.Core.Runtime;
using Zara.Core.UsagePolicy;

namespace Zara.Application.Tests;

[TestClass]
public sealed class DesktopExitUseCaseTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RestrictedExitPreservesRecoveryRegardlessOfEmergencyOrSetting(
        bool emergency, bool restartWhenAvailable)
    {
        using var fixture = new ExitFixture(restartWhenAvailable);
        await fixture.Policy.InitializeAsync();
        if (emergency)
        {
            await fixture.Policy.StartEmergencyUnlockAsync();
            Assert.AreEqual(LockState.Unlocked, fixture.Lock.CurrentState.DesiredLock);
        }

        // This is the last acknowledged state even if the process is forcibly terminated now.
        Assert.IsTrue(fixture.Continuity.CurrentAcknowledgedLease!.Decision.RestartRequired);
        Assert.IsTrue(fixture.Continuity.CurrentAcknowledgedLease.Decision.RecoverLock);
        await fixture.Exit.ExecuteAsync(fixture.ShutdownAsync);

        Assert.IsTrue(fixture.RecoverOnShutdown);
        Assert.AreEqual(0, fixture.ReleaseCount);
        Assert.IsFalse(fixture.Continuity.IsReleased);
        Assert.AreEqual(OverlayProjectionState.Hidden, fixture.Lock.CurrentState.OverlayProjection);
        Assert.IsTrue(fixture.Continuity.CurrentAcknowledgedLease!.Decision.RecoverLock);

        // A new runtime reads the same settings without inheriting the emergency timer.
        using var restarted = new ExitFixture(restartWhenAvailable);
        await restarted.Policy.InitializeAsync();
        Assert.IsFalse(restarted.Policy.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.AreEqual(LockState.Locked, restarted.Lock.CurrentState.DesiredLock);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task AvailableOrReservedTrayExitStillReleasesSupervision(
        bool reserved, bool restartWhenAvailable)
    {
        using var fixture = new ExitFixture(restartWhenAvailable, reserved);
        if (!reserved)
        {
            fixture.Clock.Hour = 19;
        }

        await fixture.Policy.InitializeAsync();
        Assert.AreEqual(restartWhenAvailable,
            fixture.Continuity.CurrentAcknowledgedLease!.Decision.RestartRequired);
        await fixture.Exit.ExecuteAsync(fixture.ShutdownAsync);

        Assert.IsFalse(fixture.RecoverOnShutdown);
        Assert.AreEqual(1, fixture.ReleaseCount);
        Assert.IsTrue(fixture.Continuity.IsReleased);
        Assert.IsFalse(fixture.Policy.CurrentSnapshot.Evaluation.LockRequiredAfterRestart);
    }

    [TestMethod]
    [DataRow(20, 21, true)]
    [DataRow(21, 22, false)]
    public async Task ExitUsesTimeAfterConfirmation(int beforeHour, int afterHour, bool recover)
    {
        using var fixture = new ExitFixture(restartWhenAvailable: false);
        fixture.Clock.Hour = beforeHour;
        await fixture.Policy.InitializeAsync();
        fixture.Clock.Hour = afterHour;

        await fixture.Exit.ExecuteAsync(fixture.ShutdownAsync);

        Assert.AreEqual(recover, fixture.RecoverOnShutdown);
        Assert.AreEqual(recover ? 0 : 1, fixture.ReleaseCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmergencyUnlockTracksUnderlyingTimeAndReservationBoundaries(bool reserved)
    {
        using var fixture = new ExitFixture(restartWhenAvailable: false, reserved);
        await fixture.Policy.InitializeAsync();
        await fixture.Policy.StartEmergencyUnlockAsync();
        Assert.AreEqual(LockState.Unlocked, fixture.Lock.CurrentState.DesiredLock);
        Assert.AreEqual(!reserved, fixture.Continuity.CurrentAcknowledgedLease!.LockRequired);

        fixture.Clock.Minute = 30;
        fixture.Clock.Hour = reserved ? 21 : 22;
        await fixture.Policy.RefreshAsync();

        Assert.IsTrue(fixture.Policy.CurrentSnapshot.Evaluation.HasActiveEmergencyUnlock);
        Assert.AreEqual(LockState.Unlocked, fixture.Lock.CurrentState.DesiredLock);
        Assert.AreEqual(reserved, fixture.Continuity.CurrentAcknowledgedLease!.LockRequired);
        Assert.AreEqual(reserved, fixture.Continuity.CurrentAcknowledgedLease.Decision.RestartRequired);
    }

    [TestMethod]
    public async Task RemovingReservationDuringEmergencyRestoresRestartProtection()
    {
        using var fixture = new ExitFixture(restartWhenAvailable: false, reserved: true);
        await fixture.Policy.InitializeAsync();
        await fixture.Policy.StartEmergencyUnlockAsync();
        Guid reservation = fixture.Settings.Reservations.Single().Id;

        await fixture.Policy.RemoveReservationAsync(reservation);

        Assert.AreEqual(LockState.Unlocked, fixture.Lock.CurrentState.DesiredLock);
        Assert.IsTrue(fixture.Continuity.CurrentAcknowledgedLease!.Decision.RecoverLock);
    }

    [TestMethod]
    [DataRow("cleanup")]
    [DataRow("publish")]
    [DataRow("release")]
    public async Task PreparationFailureNeverInitiatesShutdown(string failure)
    {
        using var fixture = new ExitFixture(restartWhenAvailable: true);
        if (failure == "release")
        {
            fixture.Clock.Hour = 19;
        }

        await fixture.Policy.InitializeAsync();
        fixture.Failure = failure;

        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Exit.ExecuteAsync(fixture.ShutdownAsync));

        Assert.IsNull(fixture.RecoverOnShutdown);
        Assert.IsFalse(fixture.Continuity.IsReleased);
        Assert.IsTrue(fixture.Continuity.CurrentAcknowledgedLease!.Decision.RestartRequired);
        if (failure != "release")
        {
            Assert.AreEqual(0, fixture.ReleaseCount);
        }
    }

    [TestMethod]
    [DataRow(false, 20, 21, 0, true)]
    [DataRow(false, 21, 22, 0, false)]
    [DataRow(true, 21, 21, 30, true)]
    public async Task ExitRechecksTimeAfterCleanupAndProjectionAcknowledgement(
        bool reserved, int initialHour, int finalHour, int finalMinute, bool recover)
    {
        using var fixture = new ExitFixture(restartWhenAvailable: false, reserved);
        fixture.Clock.Hour = initialHour;
        await fixture.Policy.InitializeAsync();
        if (reserved)
        {
            await fixture.Policy.StartEmergencyUnlockAsync();
        }

        fixture.OnPublish = _ =>
        {
            fixture.Clock.Hour = finalHour;
            fixture.Clock.Minute = finalMinute;
        };

        await fixture.Exit.ExecuteAsync(fixture.ShutdownAsync);

        Assert.AreEqual(recover, fixture.RecoverOnShutdown);
        Assert.AreEqual(recover ? 0 : 1, fixture.ReleaseCount);
        if (recover)
        {
            Assert.IsTrue(fixture.Continuity.CurrentAcknowledgedLease!.Decision.RecoverLock);
        }
    }

    [TestMethod]
    public async Task ExitPreservesAnIndependentManualLockWhenTheScheduleIsUnchanged()
    {
        using var fixture = new ExitFixture(restartWhenAvailable: false);
        fixture.Clock.Hour = 19;
        await fixture.Policy.InitializeAsync();
        await fixture.Continuity.PublishLockConditionAsync(lockRequired: true);
        await fixture.Lock.RequestLockAsync();

        await fixture.Exit.ExecuteAsync(fixture.ShutdownAsync);

        Assert.IsTrue(fixture.RecoverOnShutdown);
        Assert.AreEqual(0, fixture.ReleaseCount);
    }

    [TestMethod]
    public async Task SafetyCancellationDuringCleanupPreventsExitAndRelease()
    {
        using var fixture = new ExitFixture(restartWhenAvailable: false);
        using var cancellation = new CancellationTokenSource();
        await fixture.Policy.InitializeAsync();
        fixture.HideAsync = () =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Exit.ExecuteAsync(fixture.ShutdownAsync, cancellation.Token));

        Assert.IsNull(fixture.RecoverOnShutdown);
        Assert.AreEqual(0, fixture.ReleaseCount);
        Assert.IsFalse(fixture.Continuity.IsReleased);
        // The safety-unlock caller can now publish its newer intention without a terminal release.
        await fixture.Continuity.PublishLockConditionAsync(lockRequired: false);
        Assert.IsFalse(fixture.Continuity.CurrentAcknowledgedLease!.LockRequired);
    }

    [TestMethod]
    public async Task PolicyRefreshWaitsUntilExitPreparationCompletes()
    {
        using var fixture = new ExitFixture(restartWhenAvailable: false);
        await fixture.Policy.InitializeAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.HideAsync = async () =>
        {
            entered.SetResult();
            await finish.Task;
        };
        Task exit = fixture.Exit.ExecuteAsync(fixture.ShutdownAsync);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task refresh = fixture.Policy.RefreshAsync();
        try
        {
            Assert.IsFalse(refresh.IsCompleted);
            Assert.IsNull(fixture.RecoverOnShutdown);
        }
        finally
        {
            finish.SetResult();
        }

        await Task.WhenAll(exit, refresh).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(fixture.RecoverOnShutdown);
    }

    private sealed class ExitFixture : IDisposable, IUsagePolicyLockPort,
        IUsagePolicySettingsStore, IEmergencyUnlockPromptCatalog, ILockOverlayPort, ILockInputPort, IRestartContinuityPort
    {
        public ExitFixture(bool restartWhenAvailable, bool reserved = false)
        {
            var schedule = WeeklyUsageRestrictionSchedule.Default.WithRestriction(
                DayOfWeek.Monday, new DailyUsageRestriction(true, new TimeOnly(21, 0), new TimeOnly(22, 0)));
            Settings = new UsagePolicySettings(schedule, new EmergencyUnlockSettings(10, 0),
                reserved ? [new OutOfHoursReservation(Guid.NewGuid(), new DateOnly(2026, 8, 10),
                    new TimeOnly(21, 0), new TimeOnly(21, 30), "예약")] : []);
            Lock = new LockRuntimeUseCase(this, this);
            Continuity = new RestartContinuityUseCase(this, restartWhenAvailable);
            Policy = new UsagePolicyRuntime(this, this, this, Clock);
            Exit = new DesktopExitUseCase(Policy, Lock, Continuity);
        }

        public UsagePolicySettings Settings { get; private set; }
        public ManualClock Clock { get; } = new();
        public LockRuntimeUseCase Lock { get; }
        public RestartContinuityUseCase Continuity { get; }
        public UsagePolicyRuntime Policy { get; }
        public DesktopExitUseCase Exit { get; }
        public string? Failure { get; set; }
        public int ReleaseCount { get; private set; }
        public bool? RecoverOnShutdown { get; private set; }
        public Func<Task>? HideAsync { get; set; }
        public Action<RestartContinuityLease>? OnPublish { get; set; }

        public Task ShutdownAsync(bool recoverLock)
        {
            RecoverOnShutdown = recoverLock;
            Assert.AreEqual(OverlayProjectionState.Hidden, Lock.CurrentState.OverlayProjection);
            return Task.CompletedTask;
        }

        public async Task ApplyPolicyLockRequirementAsync(
            bool lockRequired, bool lockRequiredAfterRestart, CancellationToken cancellationToken = default)
        {
            await Continuity.PublishLockConditionAsync(lockRequiredAfterRestart, cancellationToken);
            if (lockRequired)
            {
                await Lock.RequestLockAsync(cancellationToken);
            }
            else
            {
                await Lock.RequestUnlockAsync(cancellationToken);
            }
        }

        public Task<UsagePolicySettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings);

        public Task SaveAsync(UsagePolicySettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> SelectDistinctAsync(int count, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task EnableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShowAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task HideAllAsync(CancellationToken cancellationToken) => Failure == "cleanup"
            ? Task.FromException(new IOException("cleanup failed"))
            : HideAsync?.Invoke() ?? Task.CompletedTask;

        public Task<RestartContinuityAcknowledgement> PublishAsync(
            RestartContinuityLease lease, CancellationToken cancellationToken)
        {
            OnPublish?.Invoke(lease);
            return Failure == "publish"
                ? Task.FromException<RestartContinuityAcknowledgement>(new IOException("publish failed"))
                : Task.FromResult(new RestartContinuityAcknowledgement(lease.Revision));
        }

        public Task<RestartContinuityAcknowledgement> ReleaseAsync(
            RestartContinuityRelease release, CancellationToken cancellationToken)
        {
            ReleaseCount++;
            return Failure == "release"
                ? Task.FromException<RestartContinuityAcknowledgement>(new IOException("release failed"))
                : Task.FromResult(new RestartContinuityAcknowledgement(release.Revision));
        }

        public void Dispose()
        {
            Policy.Dispose();
            Lock.Dispose();
            Continuity.Dispose();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public int Hour { get; set; } = 21;
        public int Minute { get; set; }
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 10, Hour, Minute, 0, TimeSpan.Zero);
        public override long GetTimestamp() => 0;
    }
}
