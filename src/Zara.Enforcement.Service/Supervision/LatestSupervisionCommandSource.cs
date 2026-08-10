namespace Zara.Enforcement.Service.Supervision;

/// <summary>
/// Stores only the newest immutable supervision instruction and wakes all observers when a newer
/// revision is published. This prevents repeated health or lease notifications from creating an
/// unbounded command queue.
/// </summary>
internal sealed class LatestSupervisionCommandSource : ISupervisionCommandSource
{
    private readonly object _gate = new();
    private SupervisionDirective _current;
    private TaskCompletionSource<bool> _changed = CreateChangedSignal();
    private bool _sessionEnding;

    public LatestSupervisionCommandSource(SupervisionDirective initialDirective)
    {
        _current = initialDirective;
    }

    public SupervisionDirective Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Gets whether the target logon generation has ended. This terminal state dominates every
    /// later pipe disconnect or stale lease publication.
    /// </summary>
    public bool IsSessionEnding
    {
        get
        {
            lock (_gate)
            {
                return _sessionEnding;
            }
        }
    }

    /// <summary>
    /// Publishes a caller-supplied revision when it is strictly newer than the current value.
    /// </summary>
    /// <returns><see langword="true" /> when the value became current.</returns>
    public bool TryPublish(SupervisionDirective directive)
    {
        TaskCompletionSource<bool> changed;

        lock (_gate)
        {
            if (_sessionEnding || directive.Revision <= _current.Revision)
            {
                return false;
            }

            _current = directive;
            changed = _changed;
            _changed = CreateChangedSignal();
        }

        changed.TrySetResult(true);
        return true;
    }

    /// <summary>
    /// Publishes the next locally generated revision. The Service control handler uses this for
    /// session-ending transitions; IPC adapters normally use <see cref="TryPublish" />.
    /// </summary>
    public SupervisionDirective PublishNext(
        bool restartRequired,
        SupervisionDirectiveReason reason)
    {
        TaskCompletionSource<bool> changed;
        SupervisionDirective directive;

        lock (_gate)
        {
            if (_sessionEnding)
            {
                return _current;
            }

            directive = new(
                checked(_current.Revision + 1),
                restartRequired,
                reason);
            _current = directive;
            changed = _changed;
            _changed = CreateChangedSignal();
        }

        changed.TrySetResult(true);
        return directive;
    }

    /// <summary>
    /// Atomically terminates the target logon generation and publishes the final no-restart
    /// directive. Later client-finally paths cannot overwrite this transition.
    /// </summary>
    public SupervisionDirective EndSession()
    {
        TaskCompletionSource<bool> changed;
        SupervisionDirective directive;

        lock (_gate)
        {
            if (_sessionEnding)
            {
                return _current;
            }

            _sessionEnding = true;
            directive = new(
                checked(_current.Revision + 1),
                RestartRequired: false,
                SupervisionDirectiveReason.SessionEnding);
            _current = directive;
            changed = _changed;
            _changed = CreateChangedSignal();
        }

        changed.TrySetResult(true);
        return directive;
    }

    public async ValueTask<SupervisionDirective> WaitForChangeAsync(
        long observedRevision,
        CancellationToken cancellationToken)
    {
        Task changedTask;

        lock (_gate)
        {
            if (_current.Revision > observedRevision)
            {
                return _current;
            }

            changedTask = _changed.Task;
        }

        await changedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Current;
    }

    private static TaskCompletionSource<bool> CreateChangedSignal()
    {
        return new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
