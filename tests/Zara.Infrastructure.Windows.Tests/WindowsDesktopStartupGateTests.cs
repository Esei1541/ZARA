using Zara.Infrastructure.Windows;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsDesktopStartupGateTests
{
    [TestMethod]
    public void OtherThreadCannotAcquireUntilOwnerReleasesGate()
    {
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? workerFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                using WindowsDesktopStartupGate? gate = WindowsDesktopStartupGate.TryAcquire();
                if (gate is null)
                {
                    throw new InvalidOperationException("The owner could not acquire the startup gate.");
                }
                acquired.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the startup gate.");
                }
            }
            catch (Exception exception)
            {
                workerFailure = exception;
                acquired.Set();
            }
        })
        {
            IsBackground = true,
        };
        owner.Start();
        try
        {
            Assert.IsTrue(acquired.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsNull(workerFailure);
            using WindowsDesktopStartupGate? contender = WindowsDesktopStartupGate.TryAcquire();
            Assert.IsNull(contender);
        }
        finally
        {
            release.Set();
            Assert.IsTrue(owner.Join(TimeSpan.FromSeconds(10)));
        }

        Assert.IsNull(workerFailure);
        using WindowsDesktopStartupGate? replacement = WindowsDesktopStartupGate.TryAcquire();
        Assert.IsNotNull(replacement);
    }
}
