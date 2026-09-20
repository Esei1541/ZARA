namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsShutdownNotificationTrackerTests
{
    [TestMethod]
    public void EndSessionWithoutQueryDoesNotReportCancellation()
    {
        var tracker = new WindowsShutdownNotificationTracker();
        tracker.BeginRequest(Guid.NewGuid());

        Assert.IsNull(tracker.ObserveMessage(0x0016, nint.Zero));
        Assert.IsNull(tracker.ObserveMessage(0x0113, nint.Zero));
    }

    [TestMethod]
    public void ConfirmedCancellationIsReportedOnceForQueriedAttempt()
    {
        var tracker = new WindowsShutdownNotificationTracker();
        Guid requestId = Guid.NewGuid();
        tracker.BeginRequest(requestId);

        Assert.IsNull(tracker.ObserveMessage(0x0011, nint.Zero));
        WindowsShutdownNotification? result = tracker.ObserveMessage(0x0016, nint.Zero);

        Assert.IsNotNull(result);
        Assert.AreEqual(requestId, result.RequestId);
        Assert.IsFalse(result.IsEnding);
        Assert.IsNull(tracker.ObserveMessage(0x0016, nint.Zero));
    }

    [TestMethod]
    public void CommittedSessionEndIsNotCancellation()
    {
        var tracker = new WindowsShutdownNotificationTracker();
        tracker.BeginRequest(Guid.NewGuid());
        tracker.ObserveMessage(0x0011, nint.Zero);

        WindowsShutdownNotification? result = tracker.ObserveMessage(0x0016, new nint(1));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsEnding);
    }

    [TestMethod]
    public void PreviousQueryDoesNotCarryIntoNewAttempt()
    {
        var tracker = new WindowsShutdownNotificationTracker();
        Guid firstRequestId = Guid.NewGuid();
        Guid secondRequestId = Guid.NewGuid();
        tracker.BeginRequest(firstRequestId);
        tracker.ObserveMessage(0x0011, nint.Zero);
        tracker.BeginRequest(secondRequestId);
        tracker.CompleteRequest(firstRequestId);

        Assert.IsNull(tracker.ObserveMessage(0x0016, nint.Zero));
        tracker.ObserveMessage(0x0011, nint.Zero);
        Assert.AreEqual(secondRequestId, tracker.ObserveMessage(0x0016, nint.Zero)?.RequestId);
    }

    [TestMethod]
    public void FailedRequestAndUnrelatedSessionChangesDoNotReportCancellation()
    {
        var tracker = new WindowsShutdownNotificationTracker();
        Guid requestId = Guid.NewGuid();
        tracker.BeginRequest(requestId);
        tracker.ObserveMessage(0x0011, nint.Zero);
        tracker.CompleteRequest(requestId);

        Assert.IsNull(tracker.ObserveMessage(0x0016, nint.Zero));
        tracker.ObserveMessage(0x0011, nint.Zero);
        Assert.IsNull(tracker.ObserveMessage(0x0016, nint.Zero));
    }
}
