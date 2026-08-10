using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Zara.Enforcement.Service.Supervision;

/// <summary>
/// Owns pending one-time launch tokens. The named-pipe server calls <see cref="TryReportHealthy" />
/// only after validating the exact client PID, session, token SID, path, and protocol generation.
/// </summary>
internal sealed class DesktopLaunchHandshakeRegistry : IDesktopLaunchHandshakeFactory
{
    internal static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, PendingHandshake> _pending =
        new(StringComparer.Ordinal);
    private readonly TimeSpan _healthTimeout;

    public DesktopLaunchHandshakeRegistry(TimeSpan? healthTimeout = null)
    {
        _healthTimeout = healthTimeout ?? DefaultHealthTimeout;
        if (_healthTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(healthTimeout),
                "The launch health timeout must be positive.");
        }
    }

    public ValueTask<IDesktopLaunchHandshake> CreateAsync(
        int sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            string token = RandomNumberGenerator.GetHexString(64);
            var handshake = new PendingHandshake(
                this,
                token,
                sessionId,
                _healthTimeout);
            if (_pending.TryAdd(token, handshake))
            {
                return ValueTask.FromResult<IDesktopLaunchHandshake>(handshake);
            }
        }
    }

    /// <summary>
    /// Completes and consumes a one-time token after the IPC layer has authenticated the exact
    /// launched desktop. A replay or token from another session is rejected.
    /// </summary>
    public bool TryReportHealthy(string oneTimeToken, int sessionId, int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oneTimeToken);
        if (!_pending.TryGetValue(oneTimeToken, out PendingHandshake? handshake) ||
            handshake.SessionId != sessionId ||
            handshake.ProcessId != processId ||
            processId <= 0 ||
            !_pending.TryRemove(
                new KeyValuePair<string, PendingHandshake>(oneTimeToken, handshake)))
        {
            return false;
        }

        return handshake.TrySetHealthy();
    }

    /// <summary>
    /// Checks a registration token without consuming it. The later healthy report remains the
    /// single operation that completes and removes the launch handshake.
    /// </summary>
    public bool IsPendingForProcess(string oneTimeToken, int sessionId, int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oneTimeToken);
        return processId > 0 &&
            _pending.TryGetValue(oneTimeToken, out PendingHandshake? handshake) &&
            handshake.SessionId == sessionId &&
            handshake.ProcessId == processId;
    }

    private void Remove(PendingHandshake handshake)
    {
        _pending.TryRemove(
            new KeyValuePair<string, PendingHandshake>(
                handshake.OneTimeToken,
                handshake));
    }

    private sealed class PendingHandshake : IDesktopLaunchHandshake
    {
        private readonly DesktopLaunchHandshakeRegistry _owner;
        private readonly TimeSpan _healthTimeout;
        private readonly TaskCompletionSource _healthy = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private int _processId;

        public PendingHandshake(
            DesktopLaunchHandshakeRegistry owner,
            string oneTimeToken,
            int sessionId,
            TimeSpan healthTimeout)
        {
            _owner = owner;
            OneTimeToken = oneTimeToken;
            SessionId = sessionId;
            _healthTimeout = healthTimeout;
        }

        public string OneTimeToken { get; }

        public int SessionId { get; }

        public int ProcessId => Volatile.Read(ref _processId);

        public bool TryBindProcess(int processId)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
            return Volatile.Read(ref _disposed) == 0 &&
                Interlocked.CompareExchange(ref _processId, processId, 0) == 0;
        }

        public Task WaitForHealthyAsync(CancellationToken cancellationToken)
        {
            return _healthy.Task.WaitAsync(_healthTimeout, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Remove(this);
                _healthy.TrySetCanceled();
            }

            return ValueTask.CompletedTask;
        }

        public bool TrySetHealthy()
        {
            return Volatile.Read(ref _disposed) == 0 && _healthy.TrySetResult();
        }
    }
}
