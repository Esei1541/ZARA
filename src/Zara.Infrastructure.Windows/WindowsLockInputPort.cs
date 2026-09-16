using Zara.Application.Locking;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Owns a shell-shortcut hook only while the interactive session is locked.
/// </summary>
public sealed class WindowsLockInputPort : ILockInputPort, IDisposable
{
    private readonly Func<IShellShortcutHook> _createHook;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IShellShortcutHook? _hook;
    private bool _stopping;
    private int _disposed;

    /// <summary>
    /// Creates an inactive adapter; no thread or hook is started until locking.
    /// </summary>
    public WindowsLockInputPort()
        : this(static () => new WindowsShellShortcutHook())
    {
    }

    internal WindowsLockInputPort(Func<IShellShortcutHook> createHook)
    {
        _createHook = createHook;
    }

    /// <inheritdoc />
    public async Task EnableAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_hook is not null)
            {
                if (!_stopping && !_hook.Stopped.IsCompleted)
                {
                    return;
                }

                await StopCoreAsync().ConfigureAwait(false);
            }

            IShellShortcutHook hook = _createHook();
            hook.Start();
            _hook = hook;
            // Once started, wait for a definite result. Cancelling this wait could orphan a hook.
            try
            {
                await hook.Started.ConfigureAwait(false);
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisableAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Restores input before other Desktop resources are disposed, including after startup failure.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _gate.Wait();
        try
        {
            StopCoreAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _gate.Release();
        }

        // As with LockRuntimeUseCase, leave the semaphore alive for in-flight waiters.
        GC.SuppressFinalize(this);
    }

    private async Task StopCoreAsync()
    {
        if (_hook is not { } hook)
        {
            return;
        }

        _stopping = true;
        hook.Stop();
        try
        {
            await hook.Stopped.ConfigureAwait(false);
        }
        finally
        {
            _hook = null;
            _stopping = false;
        }
    }
}

internal interface IShellShortcutHook
{
    Task Started { get; }

    Task Stopped { get; }

    void Start();

    void Stop();
}
