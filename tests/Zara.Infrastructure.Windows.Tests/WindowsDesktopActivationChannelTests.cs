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
}
