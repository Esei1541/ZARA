using Zara.Core.Runtime;

namespace Zara.Core.Tests;

[TestClass]
public sealed class RuntimeReducerTests
{
    [TestMethod]
    public void InitialStateIsUnlockedWithConfirmedHiddenProjection()
    {
        Assert.AreEqual(LockState.Unlocked, RuntimeState.Initial.DesiredLock);
        Assert.AreEqual(OverlayProjectionState.Hidden, RuntimeState.Initial.OverlayProjection);
    }

    [TestMethod]
    public void LockRequestedFromInitialStateStartsVisibleProjection()
    {
        var transition = RuntimeReducer.Reduce(RuntimeState.Initial, new LockRequested());

        AssertState(
            transition.NextState,
            LockState.Locked,
            OverlayProjectionState.ApplyingVisible);
        AssertSingleEffect(transition, OverlayVisibility.Visible);
    }

    [TestMethod]
    [DataRow(OverlayProjectionState.ApplyingVisible)]
    [DataRow(OverlayProjectionState.Visible)]
    public void LockRequestedWhenAlreadyRequestedDoesNotDuplicateVisibleEffect(
        OverlayProjectionState projection)
    {
        var state = new RuntimeState(LockState.Locked, projection);

        var transition = RuntimeReducer.Reduce(state, new LockRequested());

        Assert.AreSame(state, transition.NextState);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void VisibleProjectionSucceededCompletesLockedState()
    {
        var state = new RuntimeState(
            LockState.Locked,
            OverlayProjectionState.ApplyingVisible);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionSucceeded(OverlayVisibility.Visible));

        AssertState(transition.NextState, LockState.Locked, OverlayProjectionState.Visible);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void DuplicateVisibleSuccessInStableStateIsIgnored()
    {
        var state = new RuntimeState(LockState.Locked, OverlayProjectionState.Visible);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionSucceeded(OverlayVisibility.Visible));

        Assert.AreSame(state, transition.NextState);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void SafetyUnlockRequestedFromVisibleStateStartsHiddenProjection()
    {
        var state = new RuntimeState(LockState.Locked, OverlayProjectionState.Visible);

        var transition = RuntimeReducer.Reduce(state, new SafetyUnlockRequested());

        AssertState(
            transition.NextState,
            LockState.Unlocked,
            OverlayProjectionState.ApplyingHidden);
        AssertSingleEffect(transition, OverlayVisibility.Hidden);
    }

    [TestMethod]
    [DataRow(OverlayProjectionState.ApplyingHidden)]
    [DataRow(OverlayProjectionState.Hidden)]
    public void SafetyUnlockRequestedWhenAlreadyRequestedDoesNotDuplicateHiddenEffect(
        OverlayProjectionState projection)
    {
        var state = new RuntimeState(LockState.Unlocked, projection);

        var transition = RuntimeReducer.Reduce(state, new SafetyUnlockRequested());

        Assert.AreSame(state, transition.NextState);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void HiddenProjectionSucceededCompletesUnlockedState()
    {
        var state = new RuntimeState(
            LockState.Unlocked,
            OverlayProjectionState.ApplyingHidden);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionSucceeded(OverlayVisibility.Hidden));

        AssertState(transition.NextState, LockState.Unlocked, OverlayProjectionState.Hidden);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void SafetyUnlockRequestedWhileVisibleProjectionIsApplyingWaitsForResult()
    {
        var state = new RuntimeState(
            LockState.Locked,
            OverlayProjectionState.ApplyingVisible);

        var transition = RuntimeReducer.Reduce(state, new SafetyUnlockRequested());

        AssertState(
            transition.NextState,
            LockState.Unlocked,
            OverlayProjectionState.ApplyingVisible);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void VisibleProjectionSucceededAfterSafetyUnlockStartsHiddenProjection()
    {
        var state = new RuntimeState(
            LockState.Unlocked,
            OverlayProjectionState.ApplyingVisible);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionSucceeded(OverlayVisibility.Visible));

        AssertState(
            transition.NextState,
            LockState.Unlocked,
            OverlayProjectionState.ApplyingHidden);
        AssertSingleEffect(transition, OverlayVisibility.Hidden);
    }

    [TestMethod]
    public void VisibleProjectionFailedAfterSafetyUnlockStartsHiddenCleanup()
    {
        var state = new RuntimeState(
            LockState.Unlocked,
            OverlayProjectionState.ApplyingVisible);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionFailed(OverlayVisibility.Visible));

        AssertState(
            transition.NextState,
            LockState.Unlocked,
            OverlayProjectionState.ApplyingHidden);
        AssertSingleEffect(transition, OverlayVisibility.Hidden);
    }

    [TestMethod]
    public void LockRequestedWhileHiddenProjectionIsApplyingWaitsForResult()
    {
        var state = new RuntimeState(
            LockState.Unlocked,
            OverlayProjectionState.ApplyingHidden);

        var transition = RuntimeReducer.Reduce(state, new LockRequested());

        AssertState(
            transition.NextState,
            LockState.Locked,
            OverlayProjectionState.ApplyingHidden);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void HiddenProjectionSucceededAfterLockRequestStartsVisibleProjection()
    {
        var state = new RuntimeState(
            LockState.Locked,
            OverlayProjectionState.ApplyingHidden);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionSucceeded(OverlayVisibility.Hidden));

        AssertState(
            transition.NextState,
            LockState.Locked,
            OverlayProjectionState.ApplyingVisible);
        AssertSingleEffect(transition, OverlayVisibility.Visible);
    }

    [TestMethod]
    public void VisibleProjectionFailedLeavesUnknownWithoutAutomaticRetry()
    {
        var state = new RuntimeState(
            LockState.Locked,
            OverlayProjectionState.ApplyingVisible);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionFailed(OverlayVisibility.Visible));

        AssertState(transition.NextState, LockState.Locked, OverlayProjectionState.Unknown);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void LockRequestedFromUnknownProjectionRetriesVisibleProjection()
    {
        var state = new RuntimeState(LockState.Locked, OverlayProjectionState.Unknown);

        var transition = RuntimeReducer.Reduce(state, new LockRequested());

        AssertState(
            transition.NextState,
            LockState.Locked,
            OverlayProjectionState.ApplyingVisible);
        AssertSingleEffect(transition, OverlayVisibility.Visible);
    }

    [TestMethod]
    public void HiddenProjectionFailedLeavesUnknownWithoutAutomaticRetry()
    {
        var state = new RuntimeState(
            LockState.Unlocked,
            OverlayProjectionState.ApplyingHidden);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionFailed(OverlayVisibility.Hidden));

        AssertState(transition.NextState, LockState.Unlocked, OverlayProjectionState.Unknown);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void SafetyUnlockRequestedFromUnknownProjectionRetriesHiddenProjection()
    {
        var state = new RuntimeState(LockState.Unlocked, OverlayProjectionState.Unknown);

        var transition = RuntimeReducer.Reduce(state, new SafetyUnlockRequested());

        AssertState(
            transition.NextState,
            LockState.Unlocked,
            OverlayProjectionState.ApplyingHidden);
        AssertSingleEffect(transition, OverlayVisibility.Hidden);
    }

    [TestMethod]
    public void StaleProjectionFailureIsIgnored()
    {
        var state = new RuntimeState(LockState.Locked, OverlayProjectionState.Visible);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionFailed(OverlayVisibility.Visible));

        Assert.AreSame(state, transition.NextState);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void ConfirmedVisibleProjectionInvalidatedBecomesUnknown()
    {
        var state = new RuntimeState(LockState.Locked, OverlayProjectionState.Visible);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionInvalidated(OverlayVisibility.Visible));

        AssertState(transition.NextState, LockState.Locked, OverlayProjectionState.Unknown);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void MismatchedProjectionInvalidationIsIgnored()
    {
        var state = new RuntimeState(LockState.Locked, OverlayProjectionState.Visible);

        var transition = RuntimeReducer.Reduce(
            state,
            new OverlayProjectionInvalidated(OverlayVisibility.Hidden));

        Assert.AreSame(state, transition.NextState);
        AssertNoEffects(transition);
    }

    [TestMethod]
    public void ReduceWithNullStateThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => RuntimeReducer.Reduce(null!, new LockRequested()));
    }

    [TestMethod]
    public void ReduceWithNullEventThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => RuntimeReducer.Reduce(RuntimeState.Initial, null!));
    }

    private static void AssertState(
        RuntimeState state,
        LockState expectedLock,
        OverlayProjectionState expectedProjection)
    {
        Assert.AreEqual(expectedLock, state.DesiredLock);
        Assert.AreEqual(expectedProjection, state.OverlayProjection);
    }

    private static void AssertSingleEffect(
        RuntimeTransition transition,
        OverlayVisibility expectedVisibility)
    {
        Assert.HasCount(1, transition.Effects);
        Assert.IsInstanceOfType<ApplyOverlayVisibility>(transition.Effects[0]);
        var effect = (ApplyOverlayVisibility)transition.Effects[0];
        Assert.AreEqual(expectedVisibility, effect.Visibility);
    }

    private static void AssertNoEffects(RuntimeTransition transition)
    {
        Assert.IsEmpty(transition.Effects);
    }
}
