using Zara.Application.Locking;
using Zara.Core.Runtime;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class ShutdownPresentationStateTests
{
    [TestMethod]
    public void TerminalBeforeRequestContinuationPreventsWatchdogStart()
    {
        var state = new ShutdownPresentationState();
        Guid requestId = Guid.NewGuid();
        state.Begin(requestId);
        Assert.IsTrue(state.CanStartWatchdog(requestId));

        Assert.IsTrue(state.ObserveTerminal(requestId));

        Assert.IsTrue(state.IsPending);
        Assert.IsFalse(state.CanStartWatchdog(requestId));
    }

    [TestMethod]
    public void CompletedAttemptRejectsLateWatchdogFinallyUpdate()
    {
        var state = new ShutdownPresentationState();
        Guid requestId = Guid.NewGuid();
        state.Begin(requestId);
        state.ObserveTerminal(requestId);

        Assert.IsTrue(state.Complete(requestId));

        Assert.IsFalse(state.IsPending);
        Assert.IsFalse(state.CanStartWatchdog(requestId));
    }

    [TestMethod]
    public void UnlockDuringResultPublicationSuppressesOldResult()
    {
        var state = new ShutdownPresentationState();
        Guid requestId = Guid.NewGuid();
        var restoredIntent = new LockIntentSnapshot(LockState.Locked, 12);
        var newerUnlock = new LockIntentSnapshot(LockState.Unlocked, 13);
        state.Begin(requestId);
        state.ObserveTerminal(requestId);
        state.Complete(requestId);

        Assert.IsTrue(state.CanShowResult(requestId, restoredIntent, restoredIntent));
        Assert.IsFalse(state.CanShowResult(requestId, restoredIntent, newerUnlock));
    }

    [TestMethod]
    public void NewerAttemptSuppressesOldResultEvenAfterBothAttemptsComplete()
    {
        var state = new ShutdownPresentationState();
        Guid firstRequestId = Guid.NewGuid();
        Guid secondRequestId = Guid.NewGuid();
        var intent = new LockIntentSnapshot(LockState.Locked, 12);
        state.Begin(firstRequestId);
        state.Complete(firstRequestId);
        state.Begin(secondRequestId);

        Assert.IsFalse(state.CanShowResult(firstRequestId, intent, intent));
        state.Complete(secondRequestId);

        Assert.IsFalse(state.CanShowResult(firstRequestId, intent, intent));
        Assert.IsTrue(state.CanShowResult(secondRequestId, intent, intent));
    }

    [TestMethod]
    public void StaleCompletionCannotClearNewPendingAttempt()
    {
        var state = new ShutdownPresentationState();
        Guid firstRequestId = Guid.NewGuid();
        Guid secondRequestId = Guid.NewGuid();
        state.Begin(firstRequestId);
        state.Complete(firstRequestId);
        state.Begin(secondRequestId);

        Assert.IsFalse(state.Complete(firstRequestId));

        Assert.AreEqual(secondRequestId, state.ActiveRequestId);
        Assert.IsTrue(state.IsPending);
        Assert.IsTrue(state.CanStartWatchdog(secondRequestId));
    }

    [TestMethod]
    public void CurrentFailedRequestCanShowResultAfterCompletion()
    {
        var state = new ShutdownPresentationState();
        Guid requestId = Guid.NewGuid();
        var expectedIntent = new LockIntentSnapshot(LockState.Locked, 12);
        var currentIntent = new LockIntentSnapshot(LockState.Locked, 12);
        state.Begin(requestId);

        Assert.IsFalse(state.CanShowResult(requestId, expectedIntent, currentIntent));
        Assert.IsTrue(state.Complete(requestId));

        Assert.IsTrue(state.CanShowResult(requestId, expectedIntent, currentIntent));
    }
}
