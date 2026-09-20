using System.Runtime.ExceptionServices;
using Zara.Application.Locking;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Owns shell-shortcut suppression and Task Manager restriction while the session is locked.
/// </summary>
public sealed class WindowsLockInputPort : ILockInputPort, IDisposable
{
    private readonly Func<IShellShortcutHook> _createHook;
    private readonly ILockInputPort? _taskManagerRestriction;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IShellShortcutHook? _hook;
    private bool _stopping;
    private bool _restrictionRequested;
    private bool _restrictionEnabled;
    private int _disposed;

    /// <summary>
    /// Creates an inactive adapter; no thread or hook is started until locking.
    /// </summary>
    public WindowsLockInputPort()
        : this(static () => new WindowsShellShortcutHook())
    {
    }

    /// <summary>Combines session input suppression with the authenticated Service policy adapter.</summary>
    public WindowsLockInputPort(ILockInputPort taskManagerRestriction)
        : this(static () => new WindowsShellShortcutHook(), taskManagerRestriction)
    {
        ArgumentNullException.ThrowIfNull(taskManagerRestriction);
    }

    internal WindowsLockInputPort(
        Func<IShellShortcutHook> createHook,
        ILockInputPort? taskManagerRestriction = null)
    {
        _createHook = createHook;
        _taskManagerRestriction = taskManagerRestriction;
    }

    /// <inheritdoc />
    public async Task EnableAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
#pragma warning disable CA1031 // Try each independent lock effect while preserving every failure.
            Exception? hookFailure = null;
            try
            {
                await EnableHookAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                hookFailure = exception;
            }

            try
            {
                await RestrictTaskManagerAsync().ConfigureAwait(false);
            }
            catch (Exception restrictionFailure) when (hookFailure is not null)
            {
                throw new AggregateException(
                    "Shell shortcut suppression and Task Manager restriction both failed.",
                    hookFailure,
                    restrictionFailure);
            }
#pragma warning restore CA1031

            if (hookFailure is not null)
            {
                ExceptionDispatchInfo.Capture(hookFailure).Throw();
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
            await RestoreInputAsync().ConfigureAwait(false);
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
            RestoreInputAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _gate.Release();
        }

        // As with LockRuntimeUseCase, leave the semaphore alive for in-flight waiters.
        GC.SuppressFinalize(this);
    }

    private async Task EnableHookAsync()
    {
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

    private async Task RestrictTaskManagerAsync()
    {
        if (!_restrictionEnabled && _taskManagerRestriction is not null)
        {
            // A failed acknowledgement can still follow a successful Windows write.
            _restrictionRequested = true;
            await _taskManagerRestriction.EnableAsync(CancellationToken.None).ConfigureAwait(false);
            _restrictionEnabled = true;
        }
    }

    private async Task RestoreInputAsync()
    {
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_restrictionRequested && _taskManagerRestriction is not null)
            {
                // A failed restore may already have removed the restriction.
                _restrictionEnabled = false;
                await _taskManagerRestriction.DisableAsync(CancellationToken.None).ConfigureAwait(false);
                _restrictionRequested = false;
            }
        }
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
