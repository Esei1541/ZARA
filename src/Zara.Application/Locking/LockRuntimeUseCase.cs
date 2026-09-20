using System.Runtime.ExceptionServices;
using Zara.Core.Runtime;

namespace Zara.Application.Locking;

/// <summary>
/// Serializes runtime requests, coordinates overlay and input adapters, and feeds every adapter
/// result back into the reducer. Visible/hidden confirmation includes the associated input effect.
/// </summary>
/// <remarks>
/// Adapter failures are recorded as an unknown projection and propagated to the caller. Successful
/// effects remain active; failed lock effects are retried once per minute until recovery or unlock.
/// </remarks>
public sealed class LockRuntimeUseCase : ILockRuntimeUseCase, IDisposable
{
    private readonly ILockOverlayPort _overlayPort;
    private readonly ILockInputPort _inputPort;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private LockRecoveryState _recovery = new(false, 0);
    private bool _overlayConfirmed;
    private bool _inputConfirmed;
    private long _lastFailureTimestamp;
    private CancellationTokenSource? _recoveryCancellation;
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
        : this(overlayPort, inputPort, TimeProvider.System, Task.Delay)
    {
    }

    internal LockRuntimeUseCase(
        ILockOverlayPort overlayPort,
        ILockInputPort inputPort,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(overlayPort);
        ArgumentNullException.ThrowIfNull(inputPort);
        _overlayPort = overlayPort;
        _inputPort = inputPort;
        _timeProvider = timeProvider;
        _delay = delay;
    }

    /// <summary>Notifies the host when a lock failure episode starts or ends.</summary>
    public event EventHandler<LockRecoveryState>? RecoveryStateChanged;

    /// <summary>Gets the current automatic recovery episode.</summary>
    public LockRecoveryState CurrentRecovery => Volatile.Read(ref _recovery);

    /// <summary>The interval between failed lock attempts.</summary>
    public static TimeSpan RecoveryInterval => TimeSpan.FromMinutes(1);

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

        _lifetime.Cancel();

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

            if (runtimeEvent is OverlayProjectionInvalidated)
            {
                _overlayConfirmed = false;
            }

            if (requestedIntent == LockState.Unlocked)
            {
                SetRecovery(false);
            }

            // Schedule refreshes and repeated commands must not bypass the one-minute delay.
            if (runtimeEvent is LockRequested && CurrentRecovery.IsRecovering &&
                RemainingRecoveryDelay() > TimeSpan.Zero)
            {
                return true;
            }

            await ProcessEventsAsync(runtimeEvent, cancellationToken).ConfigureAwait(false);
            if (runtimeEvent is OverlayProjectionInvalidated &&
                CurrentIntent.DesiredLock == LockState.Locked)
            {
                BeginRecovery(recordAttempt: false);
            }
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

        if (CurrentIntent.DesiredLock == LockState.Locked)
        {
            if (firstFailure is not null)
            {
                BeginRecovery();
            }
            else if (CurrentState.OverlayProjection == OverlayProjectionState.Visible)
            {
                SetRecovery(false);
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
            if (!_overlayConfirmed)
            {
                await _overlayPort.ShowAllAsync(cancellationToken).ConfigureAwait(false);
                _overlayConfirmed = true;
            }

            if (!_inputConfirmed)
            {
                await _inputPort.EnableAsync(cancellationToken).ConfigureAwait(false);
                _inputConfirmed = true;
            }
        }
        else
        {
            // Restore input even if subsequent window cleanup fails.
            _inputConfirmed = false;
            await _inputPort.DisableAsync(cancellationToken).ConfigureAwait(false);
            _overlayConfirmed = false;
            await _overlayPort.HideAllAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void BeginRecovery(bool recordAttempt = true)
    {
        bool wasRecovering = CurrentRecovery.IsRecovering;
        if (recordAttempt || !wasRecovering)
        {
            _lastFailureTimestamp = _timeProvider.GetTimestamp();
        }

        SetRecovery(true);
        if (!wasRecovering)
        {
            _recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _ = RunRecoveryAsync(CurrentRecovery.Episode, _recoveryCancellation);
        }
    }

    private void SetRecovery(bool active)
    {
        LockRecoveryState previous = CurrentRecovery;
        if (previous.IsRecovering == active)
        {
            return;
        }

        var next = new LockRecoveryState(active, active ? checked(previous.Episode + 1) : previous.Episode);
        Volatile.Write(ref _recovery, next);
        if (!active)
        {
            CancellationTokenSource? cancellation = _recoveryCancellation;
            _recoveryCancellation = null;
            cancellation?.Cancel();
        }

        RecoveryStateChanged?.Invoke(this, next);
    }

    private TimeSpan RemainingRecoveryDelay() =>
        RecoveryInterval - _timeProvider.GetElapsedTime(_lastFailureTimestamp);

    private async Task RunRecoveryAsync(long episode, CancellationTokenSource cancellation)
    {
        // Leave the request gate before beginning the timer loop, even with a synchronous test clock.
        await Task.Yield();
        try
        {
            while (!cancellation.IsCancellationRequested && CurrentRecovery.IsRecovering)
            {
                TimeSpan remaining = RemainingRecoveryDelay();
                if (remaining > TimeSpan.Zero)
                {
                    await _delay(remaining, cancellation.Token).ConfigureAwait(false);
                }

                await _requestGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    if (!CurrentRecovery.IsRecovering || CurrentRecovery.Episode != episode ||
                        CurrentIntent.DesiredLock != LockState.Locked)
                    {
                        return;
                    }

                    if (RemainingRecoveryDelay() > TimeSpan.Zero)
                    {
                        continue;
                    }

#pragma warning disable CA1031 // Adapter failures remain visible in recovery state and are retried later.
                    try
                    {
                        await ProcessEventsAsync(new LockRequested(), cancellation.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // ProcessEventsAsync recorded the failure and the next retry time.
                    }
#pragma warning restore CA1031
                }
                finally
                {
                    _requestGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Disposal stops an outstanding delay without starting another lock attempt.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record RuntimeSnapshot(
        RuntimeState State,
        LockState DesiredIntent,
        long IntentRevision);
}
