using System.Runtime.ExceptionServices;
using Zara.Core.Runtime;

namespace Zara.Application.Locking;

/// <summary>
/// Serializes runtime requests, coordinates overlay and input adapters, and feeds every adapter
/// result back into the reducer. Visible/hidden confirmation includes the associated input effect.
/// </summary>
/// <remarks>
/// Adapter failures and cancellations are recorded as an unknown projection and then propagated to
/// the caller. The use case does not choose an automatic retry or partial-failure product policy.
/// </remarks>
public sealed class LockRuntimeUseCase : ILockRuntimeUseCase, IDisposable
{
    private readonly ILockOverlayPort _overlayPort;
    private readonly ILockInputPort _inputPort;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private RuntimeSnapshot _currentSnapshot = new(
        RuntimeState.Initial,
        DesiredIntent: LockState.Unlocked,
        IntentRevision: 0);
    private int _disposed;

    /// <summary>
    /// Initializes a lock runtime using the supplied overlay projection.
    /// </summary>
    /// <param name="overlayPort">The adapter that owns overlay windows and topology reconciliation.</param>
    /// <param name="inputPort">The adapter that restricts shell shortcuts while locked.</param>
    public LockRuntimeUseCase(ILockOverlayPort overlayPort, ILockInputPort inputPort)
    {
        ArgumentNullException.ThrowIfNull(overlayPort);
        ArgumentNullException.ThrowIfNull(inputPort);
        _overlayPort = overlayPort;
        _inputPort = inputPort;
    }

    /// <inheritdoc />
    public RuntimeState CurrentState => Volatile.Read(ref _currentSnapshot).State;

    /// <inheritdoc />
    public LockIntentSnapshot CurrentIntent
    {
        get
        {
            RuntimeSnapshot snapshot = Volatile.Read(ref _currentSnapshot);
            return new LockIntentSnapshot(snapshot.DesiredIntent, snapshot.IntentRevision);
        }
    }

    /// <inheritdoc />
    public Task RequestLockAsync(CancellationToken cancellationToken = default) =>
        DispatchIntentAsync(new LockRequested(), cancellationToken);

    /// <inheritdoc />
    public Task RequestUnlockAsync(CancellationToken cancellationToken = default) =>
        DispatchIntentAsync(new SafetyUnlockRequested(), cancellationToken);

    /// <inheritdoc />
    public Task RequestDevelopmentUnlockAsync(CancellationToken cancellationToken = default) =>
        RequestUnlockAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> RestoreLockIfIntentRevisionAsync(
        long expectedIntentRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedIntentRevision);
        return DispatchCoreAsync(
            new LockRequested(),
            requestedIntent: LockState.Locked,
            expectedIntentRevision,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task ReportOverlayProjectionInvalidatedAsync(
        OverlayVisibility visibility,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(new OverlayProjectionInvalidated(visibility), cancellationToken);

    /// <inheritdoc />
    public async Task PrepareForExitAsync(CancellationToken cancellationToken = default)
    {
        await DispatchIntentAsync(new SafetyUnlockRequested(), cancellationToken).ConfigureAwait(false);

        if (CurrentState.OverlayProjection != OverlayProjectionState.Hidden)
        {
            throw new InvalidOperationException(
                "The overlay projection must be confirmed hidden before application exit.");
        }
    }

    /// <summary>
    /// Prevents new requests while allowing an already serialized request to release its gate.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // SemaphoreSlim is intentionally left for GC. Disposing it here can race an in-flight
        // request whose finally block must still release the gate without replacing its result.
        GC.SuppressFinalize(this);
    }

    private async Task DispatchAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        await DispatchCoreAsync(
                runtimeEvent,
                requestedIntent: null,
                expectedIntentRevision: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DispatchIntentAsync(
        RuntimeEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        await DispatchCoreAsync(
                runtimeEvent,
                requestedIntent: runtimeEvent switch
                {
                    LockRequested => LockState.Locked,
                    SafetyUnlockRequested => LockState.Unlocked,
                    _ => throw new InvalidOperationException(
                        $"Unsupported intent event: {runtimeEvent.GetType().FullName}"),
                },
                expectedIntentRevision: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> DispatchCoreAsync(
        RuntimeEvent runtimeEvent,
        LockState? requestedIntent,
        long? expectedIntentRevision,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            RuntimeSnapshot snapshot = Volatile.Read(ref _currentSnapshot);
            if (expectedIntentRevision is long expectedRevision &&
                snapshot.IntentRevision != expectedRevision)
            {
                return false;
            }

            if (requestedIntent is LockState nextIntent)
            {
                Volatile.Write(
                    ref _currentSnapshot,
                    snapshot with
                    {
                        DesiredIntent = nextIntent,
                        IntentRevision = checked(snapshot.IntentRevision + 1),
                    });
            }

            await ProcessEventsAsync(runtimeEvent, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task ProcessEventsAsync(
        RuntimeEvent initialEvent,
        CancellationToken cancellationToken)
    {
        var pendingEvents = new Queue<RuntimeEvent>();
        ExceptionDispatchInfo? firstFailure = null;
        pendingEvents.Enqueue(initialEvent);

        while (pendingEvents.TryDequeue(out var runtimeEvent))
        {
            RuntimeSnapshot snapshot = Volatile.Read(ref _currentSnapshot);
            var transition = RuntimeReducer.Reduce(snapshot.State, runtimeEvent);
            Volatile.Write(
                ref _currentSnapshot,
                snapshot with { State = transition.NextState });

            foreach (var effect in transition.Effects)
            {
                if (effect is not ApplyOverlayVisibility overlayEffect)
                {
                    throw new InvalidOperationException(
                        $"Unsupported runtime effect: {effect.GetType().FullName}");
                }

#pragma warning disable CA1031 // Every adapter failure must be returned to Core before propagation.
                try
                {
                    await ApplyOverlayEffectAsync(overlayEffect, cancellationToken).ConfigureAwait(false);
                    pendingEvents.Enqueue(new OverlayProjectionSucceeded(overlayEffect.Visibility));
                }
                catch (Exception exception)
                {
                    firstFailure ??= ExceptionDispatchInfo.Capture(exception);
                    pendingEvents.Enqueue(new OverlayProjectionFailed(overlayEffect.Visibility));
                }
#pragma warning restore CA1031
            }
        }

        firstFailure?.Throw();
    }

    private async Task ApplyOverlayEffectAsync(
        ApplyOverlayVisibility effect,
        CancellationToken cancellationToken)
    {
        if (effect.Visibility == OverlayVisibility.Visible)
        {
            // Never restrict input before the user has an overlay with an unlock control.
            await _overlayPort.ShowAllAsync(cancellationToken).ConfigureAwait(false);
            await _inputPort.EnableAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Restore input even if subsequent window cleanup fails.
            await _inputPort.DisableAsync(cancellationToken).ConfigureAwait(false);
            await _overlayPort.HideAllAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record RuntimeSnapshot(
        RuntimeState State,
        LockState DesiredIntent,
        long IntentRevision);
}
