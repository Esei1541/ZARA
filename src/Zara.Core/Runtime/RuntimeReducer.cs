namespace Zara.Core.Runtime;

/// <summary>
/// Reduces runtime events into deterministic state transitions and declarative overlay effects.
/// </summary>
public static class RuntimeReducer
{
    private static readonly IReadOnlyList<RuntimeEffect> EmptyEffects =
        Array.Empty<RuntimeEffect>();

    /// <summary>
    /// Processes one event without reading time or performing external I/O.
    /// </summary>
    /// <param name="state">The current immutable runtime state.</param>
    /// <param name="runtimeEvent">The event to process.</param>
    /// <returns>The next state and the external effects required to project it.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="runtimeEvent"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="runtimeEvent"/> is not an event supported by this reducer.
    /// </exception>
    public static RuntimeTransition Reduce(RuntimeState state, RuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        return runtimeEvent switch
        {
            LockRequested => HandleLockRequest(state, LockState.Locked),
            SafetyUnlockRequested => HandleLockRequest(state, LockState.Unlocked),
            OverlayProjectionSucceeded succeeded => HandleProjectionSucceeded(state, succeeded.Visibility),
            OverlayProjectionFailed failed => HandleProjectionFailed(state, failed.Visibility),
            OverlayProjectionInvalidated invalidated =>
                HandleProjectionInvalidated(state, invalidated.Visibility),
            _ => throw new ArgumentOutOfRangeException(
                nameof(runtimeEvent),
                runtimeEvent.GetType().FullName,
                "Unsupported runtime event."),
        };
    }

    private static RuntimeTransition HandleLockRequest(RuntimeState state, LockState requestedLock)
    {
        var requestedState = state.DesiredLock == requestedLock
            ? state
            : state with { DesiredLock = requestedLock };

        return Reconcile(requestedState);
    }

    private static RuntimeTransition HandleProjectionSucceeded(
        RuntimeState state,
        OverlayVisibility visibility)
    {
        var expectedProjection = GetApplyingProjection(visibility);
        if (state.OverlayProjection != expectedProjection)
        {
            return Unchanged(state);
        }

        var confirmedProjection = visibility == OverlayVisibility.Visible
            ? OverlayProjectionState.Visible
            : OverlayProjectionState.Hidden;

        return Reconcile(state with { OverlayProjection = confirmedProjection });
    }

    private static RuntimeTransition HandleProjectionFailed(
        RuntimeState state,
        OverlayVisibility visibility)
    {
        var expectedProjection = GetApplyingProjection(visibility);
        if (state.OverlayProjection != expectedProjection)
        {
            return Unchanged(state);
        }

        var unknownState = state with { OverlayProjection = OverlayProjectionState.Unknown };
        var desiredVisibility = GetDesiredVisibility(unknownState.DesiredLock);

        // A failure is not retried automatically. If the desired state changed while the failed
        // operation was in flight, one opposite reconciliation is still required for safety.
        return desiredVisibility == visibility
            ? Unchanged(unknownState)
            : Reconcile(unknownState);
    }

    private static RuntimeTransition HandleProjectionInvalidated(
        RuntimeState state,
        OverlayVisibility visibility)
    {
        var confirmedProjection = visibility == OverlayVisibility.Visible
            ? OverlayProjectionState.Visible
            : OverlayProjectionState.Hidden;

        return state.OverlayProjection == confirmedProjection
            ? Unchanged(state with { OverlayProjection = OverlayProjectionState.Unknown })
            : Unchanged(state);
    }

    private static RuntimeTransition Reconcile(RuntimeState state)
    {
        var desiredVisibility = GetDesiredVisibility(state.DesiredLock);

        return (desiredVisibility, state.OverlayProjection) switch
        {
            (OverlayVisibility.Visible, OverlayProjectionState.Hidden or OverlayProjectionState.Unknown) =>
                StartProjection(state, OverlayVisibility.Visible),
            (OverlayVisibility.Hidden, OverlayProjectionState.Visible or OverlayProjectionState.Unknown) =>
                StartProjection(state, OverlayVisibility.Hidden),
            _ => Unchanged(state),
        };
    }

    private static RuntimeTransition StartProjection(
        RuntimeState state,
        OverlayVisibility visibility)
    {
        var applyingState = state with { OverlayProjection = GetApplyingProjection(visibility) };
        RuntimeEffect[] effects = [new ApplyOverlayVisibility(visibility)];
        return new RuntimeTransition(applyingState, effects);
    }

    private static RuntimeTransition Unchanged(RuntimeState state) =>
        new(state, EmptyEffects);

    private static OverlayVisibility GetDesiredVisibility(LockState lockState) =>
        lockState == LockState.Locked
            ? OverlayVisibility.Visible
            : OverlayVisibility.Hidden;

    private static OverlayProjectionState GetApplyingProjection(OverlayVisibility visibility) =>
        visibility == OverlayVisibility.Visible
            ? OverlayProjectionState.ApplyingVisible
            : OverlayProjectionState.ApplyingHidden;
}
