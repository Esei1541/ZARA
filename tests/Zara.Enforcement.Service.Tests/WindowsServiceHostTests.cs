using System.Security.Principal;
using Zara.Enforcement.Service;

namespace Zara.Enforcement.Service.Tests;

[TestClass]
public sealed class WindowsServiceHostTests
{
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
}
