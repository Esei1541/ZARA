using Zara.Application.Locking;

namespace Zara.Desktop;

/// <summary>Keeps delayed window updates attached to their originating shutdown attempt.</summary>
internal sealed class ShutdownPresentationState
{
    private Guid _latestRequestId;
    private bool _terminalObserved;

    internal Guid ActiveRequestId { get; private set; }

    internal bool IsPending => ActiveRequestId != Guid.Empty;

    internal void Begin(Guid requestId)
    {
        _latestRequestId = requestId;
        ActiveRequestId = requestId;
        _terminalObserved = false;
    }

    internal bool ObserveTerminal(Guid requestId)
    {
        if (ActiveRequestId != requestId)
        {
            return false;
        }

        _terminalObserved = true;
        return true;
    }

    internal bool CanStartWatchdog(Guid requestId) =>
        ActiveRequestId == requestId && !_terminalObserved;

    internal bool Complete(Guid requestId)
    {
        if (ActiveRequestId != requestId)
        {
            return false;
        }

        ActiveRequestId = Guid.Empty;
        return true;
    }

    internal bool CanShowResult(Guid requestId, LockIntentSnapshot expected, LockIntentSnapshot current) =>
        !IsPending && _latestRequestId == requestId && expected == current;
}
