namespace Zara.Infrastructure.Windows;

/// <summary>
/// Associates a native end-session result with the ZARA shutdown attempt that observed its query.
/// The desktop forwards messages from a persistent top-level window on its existing UI thread.
/// </summary>
public sealed class WindowsShutdownNotificationTracker
{
    private const int QueryEndSessionMessage = 0x0011;
    private const int EndSessionMessage = 0x0016;
    private Guid _activeRequestId;
    private Guid _queriedRequestId;

    /// <summary>Starts observing messages for one explicit shutdown attempt.</summary>
    public void BeginRequest(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty shutdown request identity is required.", nameof(requestId));
        }

        _activeRequestId = requestId;
        _queriedRequestId = Guid.Empty;
    }

    /// <summary>Stops observing an attempt after a platform request failure or explicit cleanup.</summary>
    public void CompleteRequest(Guid requestId)
    {
        if (_activeRequestId == requestId)
        {
            _activeRequestId = Guid.Empty;
            _queriedRequestId = Guid.Empty;
        }
    }

    /// <summary>
    /// Returns a terminal result only after a query was observed for the current attempt.
    /// A timeout, unrelated message, or end-session message without a matching query returns null.
    /// This observer does not control the window procedure's response to Windows.
    /// </summary>
    public WindowsShutdownNotification? ObserveMessage(int message, nint wParam)
    {
        if (message == QueryEndSessionMessage)
        {
            _queriedRequestId = _activeRequestId;
            return null;
        }

        if (message != EndSessionMessage || _activeRequestId == Guid.Empty ||
            _queriedRequestId != _activeRequestId)
        {
            return null;
        }

        var notification = new WindowsShutdownNotification(
            _activeRequestId,
            IsEnding: wParam != nint.Zero);
        CompleteRequest(_activeRequestId);
        return notification;
    }
}

/// <summary>A Windows end-session result observed for an active shutdown request.</summary>
/// <param name="RequestId">The ZARA shutdown attempt active when the session query arrived.</param>
/// <param name="IsEnding">True when Windows commits the session end; false when it cancels it.</param>
public sealed record WindowsShutdownNotification(Guid RequestId, bool IsEnding);
