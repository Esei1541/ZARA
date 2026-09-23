using Zara.Infrastructure.Windows;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsDesktopActivationChannelTests
{
    [TestMethod]
    public async Task LaterLaunchActivatesPrimaryAndPrimaryCanBeReacquiredAfterDispose()
    {
        WindowsDesktopActivationChannel? first = await WindowsDesktopActivationChannel
            .AcquireOrActivateAsync();
        Assert.IsNotNull(first);
        var activated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        first.StartListening(() =>
        {
            activated.TrySetResult();
            return Task.CompletedTask;
        });

        WindowsDesktopActivationChannel? second = await WindowsDesktopActivationChannel
            .AcquireOrActivateAsync();

        Assert.IsNull(second);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.Dispose();

        using WindowsDesktopActivationChannel? replacement =
            await WindowsDesktopActivationChannel.AcquireOrActivateAsync();
        Assert.IsNotNull(replacement);
    }

    [TestMethod]
    public async Task ActivationAcknowledgementWaitsForCallbackCompletion()
    {
        using WindowsDesktopActivationChannel primary = WindowsDesktopActivationChannel.AcquirePrimary();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(async () =>
        {
            entered.TrySetResult();
            await finish.Task;
        });

        Task<bool> activation = WindowsDesktopActivationChannel.TryActivateExistingAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(activation.IsCompleted);
        }
        finally
        {
            finish.TrySetResult();
        }

        Assert.IsTrue(await activation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task FailedCallbackDoesNotAcknowledgeAndPrimaryContinuesListening()
    {
        using WindowsDesktopActivationChannel primary = WindowsDesktopActivationChannel.AcquirePrimary();
        int calls = 0;
        primary.StartListening(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("activation failed");
            }

            return Task.CompletedTask;
        });

        _ = await Assert.ThrowsAsync<IOException>(
            () => WindowsDesktopActivationChannel.TryActivateExistingAsync());

        using var retryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool activated = false;
        while (!activated)
        {
            activated = await WindowsDesktopActivationChannel.TryActivateExistingAsync(retryDeadline.Token);
            if (!activated)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), retryDeadline.Token);
            }
        }

        Assert.IsTrue(activated);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task CancelledActivationDoesNotReportSuccess()
    {
        using WindowsDesktopActivationChannel primary = WindowsDesktopActivationChannel.AcquirePrimary();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(async () =>
        {
            entered.TrySetResult();
            await finish.Task;
        });
        using var cancellation = new CancellationTokenSource();

        Task<bool> activation = WindowsDesktopActivationChannel.TryActivateExistingAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => activation);
        }
        finally
        {
            finish.TrySetResult();
        }
    }
}
