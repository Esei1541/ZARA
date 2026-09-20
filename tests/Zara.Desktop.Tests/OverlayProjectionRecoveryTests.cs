using Zara.Desktop.Overlays;
using Zara.Infrastructure.Windows;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class OverlayProjectionRecoveryTests
{
    private static readonly string[] ExpectedInitialAttempts = ["working", "failed", "remaining"];
    private static readonly string[] ExpectedTopologyAttempts = ["working", "added"];
    private static readonly string[] ExpectedRetryAttempts = ["working", "failed", "added"];
    private static readonly string[] ExpectedRemovedDisplay = ["removed"];
    private static readonly string[] ExpectedRemovedDisplays = ["removed", "another-removed"];

    [TestMethod]
    public void FailedDisplayDoesNotCloseSuccessfulDisplaysOrStopRemainingAttempts()
    {
        var displays = CreateDisplays("working", "failed", "remaining");
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projected = new List<string>();
        var closed = new List<string>();

        AggregateException exception = Assert.ThrowsExactly<AggregateException>(() =>
            WpfLockOverlayPort.ReconcileDisplays(
                displays,
                ["working"],
                failures,
                retryFailures: true,
                display =>
                {
                    projected.Add(display.DeviceName);
                    if (display.DeviceName == "failed")
                    {
                        throw new InvalidOperationException("Display unavailable.");
                    }
                },
                closed.Add));

        CollectionAssert.AreEqual(ExpectedInitialAttempts, projected);
        Assert.HasCount(1, exception.InnerExceptions);
        Assert.IsTrue(failures.SetEquals(["failed"]));
        Assert.IsEmpty(closed);
    }

    [TestMethod]
    public void TopologyEventsSkipFailedDisplayUntilExplicitRetrySucceeds()
    {
        var displays = CreateDisplays("working", "failed", "added");
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "failed" };
        var projected = new List<string>();
        var closed = new List<string>();

        WpfLockOverlayPort.ReconcileDisplays(
            displays,
            ["working", "removed"],
            failures,
            retryFailures: false,
            display => projected.Add(display.DeviceName),
            closed.Add);

        CollectionAssert.AreEqual(ExpectedTopologyAttempts, projected);
        CollectionAssert.AreEqual(ExpectedRemovedDisplay, closed);
        Assert.Contains("failed", failures);

        projected.Clear();
        WpfLockOverlayPort.ReconcileDisplays(
            displays,
            ["working", "added"],
            failures,
            retryFailures: true,
            display => projected.Add(display.DeviceName),
            closed.Add);

        CollectionAssert.AreEqual(ExpectedRetryAttempts, projected);
        Assert.IsEmpty(failures);
    }

    [TestMethod]
    public void RemovedDisplayCloseFailureIsRetriedOnlyByExplicitRequest()
    {
        var displays = CreateDisplays("working");
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var closed = new List<string>();

        Assert.ThrowsExactly<AggregateException>(() =>
            WpfLockOverlayPort.ReconcileDisplays(
                displays,
                ["removed", "another-removed"],
                failures,
                retryFailures: true,
                _ => { },
                deviceName =>
                {
                    closed.Add(deviceName);
                    if (deviceName == "removed")
                    {
                        throw new InvalidOperationException("Close failed.");
                    }
                }));

        CollectionAssert.AreEqual(ExpectedRemovedDisplays, closed);
        Assert.IsTrue(failures.SetEquals(["removed"]));

        closed.Clear();
        WpfLockOverlayPort.ReconcileDisplays(
            displays, ["removed"], failures, false, _ => { }, closed.Add);
        Assert.IsEmpty(closed);

        WpfLockOverlayPort.ReconcileDisplays(
            displays, ["removed"], failures, true, _ => { }, closed.Add);
        CollectionAssert.AreEqual(ExpectedRemovedDisplay, closed);
        Assert.IsEmpty(failures);
    }

    private static Dictionary<string, DisplaySnapshot> CreateDisplays(params string[] deviceNames) =>
        deviceNames.ToDictionary(
            deviceName => deviceName,
            deviceName => new DisplaySnapshot(deviceName, new PixelBounds(0, 0, 100, 100), false),
            StringComparer.OrdinalIgnoreCase);
}
