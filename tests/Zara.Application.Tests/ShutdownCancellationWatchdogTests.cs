using Zara.Application.SystemPower;

namespace Zara.Application.Tests;

[TestClass]
public sealed class ShutdownCancellationWatchdogTests
{
    [TestMethod]
    public async Task DefaultDelayIsFailClosedAndRecoveryRunsAfterIt()
    {
        TimeSpan? observedDelay = null;
        var useCase = new FakeShutdownUseCase();
        var watchdog = new ShutdownCancellationWatchdog(
            useCase,
            (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observedDelay = delay;
                return Task.CompletedTask;
            });
        Guid requestId = Guid.NewGuid();

        ShutdownCancellationRecoveryResult result = await watchdog
            .RecoverIfStillAliveAsync(requestId, CancellationToken.None);

        Assert.AreEqual(TimeSpan.FromMilliseconds(250), observedDelay);
        Assert.IsTrue(ShutdownCancellationWatchdog.DefaultDelay <= TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(requestId, useCase.SafetyRecoveryRequestId);
        Assert.AreEqual(ShutdownCancellationRecoveryResult.LockRestored, result);
    }

    [TestMethod]
    public async Task CancellationBeforeDelayExpiresDoesNotInvokeRecovery()
    {
        using var cancellation = new CancellationTokenSource();
        var useCase = new FakeShutdownUseCase();
        var watchdog = new ShutdownCancellationWatchdog(
            useCase,
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        Task<ShutdownCancellationRecoveryResult> recovery = watchdog.RecoverIfStillAliveAsync(
            Guid.NewGuid(),
            cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => recovery);
        Assert.AreEqual(Guid.Empty, useCase.SafetyRecoveryRequestId);
    }

    private sealed class FakeShutdownUseCase : ISystemShutdownUseCase
    {
        public Guid SafetyRecoveryRequestId { get; private set; }

        public Task RequestShutdownAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RequestShutdownAsync(
            Guid requestId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ShutdownCancellationRecoveryResult> HandleShutdownCancellationAsync() =>
            throw new NotSupportedException();

        public Task<ShutdownCancellationRecoveryResult> HandleShutdownCancellationAsync(
            Guid requestId) => throw new NotSupportedException();

        public Task<ShutdownCancellationRecoveryResult> RestoreLockWhileShutdownPendingAsync(Guid requestId)
        {
            SafetyRecoveryRequestId = requestId;
            return Task.FromResult(ShutdownCancellationRecoveryResult.LockRestored);
        }
    }
}
