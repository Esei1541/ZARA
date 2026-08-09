using System.Runtime.ExceptionServices;
using Zara.Core.Runtime;

namespace Zara.Application.Locking;

/// <summary>
/// Serializes runtime requests, executes reducer effects through an overlay port, and feeds every
/// adapter result back into the reducer.
/// </summary>
/// <remarks>
/// Adapter failures and cancellations are recorded as an unknown projection and then propagated to
/// the caller. The use case does not choose an automatic retry or partial-failure product policy.
/// </remarks>
public sealed class LockRuntimeUseCase : ILockRuntimeUseCase, IDisposable
{
    private readonly ILockOverlayPort _overlayPort;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private RuntimeState _currentState = RuntimeState.Initial;
    private int _disposed;

    /// <summary>
    /// Initializes a lock runtime using the supplied overlay projection.
    /// </summary>
    /// <param name="overlayPort">The adapter that owns overlay windows and topology reconciliation.</param>
    public LockRuntimeUseCase(ILockOverlayPort overlayPort)
    {
        ArgumentNullException.ThrowIfNull(overlayPort);
        _overlayPort = overlayPort;
    }

    /// <inheritdoc />
    public RuntimeState CurrentState => Volatile.Read(ref _currentState);

    /// <inheritdoc />
    public Task RequestLockAsync(CancellationToken cancellationToken = default) =>
        DispatchAsync(new LockRequested(), cancellationToken);

    /// <inheritdoc />
    public Task RequestDevelopmentUnlockAsync(CancellationToken cancellationToken = default) =>
        DispatchAsync(new SafetyUnlockRequested(), cancellationToken);

    /// <inheritdoc />
    public Task ReportOverlayProjectionInvalidatedAsync(
        OverlayVisibility visibility,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(new OverlayProjectionInvalidated(visibility), cancellationToken);

    /// <inheritdoc />
    public async Task PrepareForExitAsync(CancellationToken cancellationToken = default)
    {
        await DispatchAsync(new SafetyUnlockRequested(), cancellationToken).ConfigureAwait(false);

        if (CurrentState.OverlayProjection != OverlayProjectionState.Hidden)
        {
            throw new InvalidOperationException(
                "The overlay projection must be confirmed hidden before application exit.");
        }
    }

    /// <summary>
    /// Releases the request-serialization resource owned by this use case.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _requestGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task DispatchAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            await ProcessEventsAsync(runtimeEvent, cancellationToken).ConfigureAwait(false);
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
            var transition = RuntimeReducer.Reduce(CurrentState, runtimeEvent);
            Volatile.Write(ref _currentState, transition.NextState);

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

    private Task ApplyOverlayEffectAsync(
        ApplyOverlayVisibility effect,
        CancellationToken cancellationToken) =>
        effect.Visibility == OverlayVisibility.Visible
            ? _overlayPort.ShowAllAsync(cancellationToken)
            : _overlayPort.HideAllAsync(cancellationToken);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
