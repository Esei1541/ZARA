using Microsoft.Win32.SafeHandles;
using Zara.Enforcement.Service.Supervision;

namespace Zara.Enforcement.Service.Windows;

/// <summary>
/// Waits on the exact process handle returned by CreateProcessAsUser and owns the corresponding
/// kill-on-close job for the complete launch generation.
/// </summary>
internal sealed class WindowsSupervisedProcess : ISupervisedProcess
{
    private readonly SafeKernelHandle _process;
    private readonly SafeKernelHandle _job;
    private readonly ProcessWaitHandle _waitHandle;
    private readonly IDesktopLaunchHandshake _handshake;
    private readonly Task _ready;
    private readonly TaskCompletionSource _exited = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RegisteredWaitHandle _registeredWait;
    private int _disposed;

    public WindowsSupervisedProcess(
        int processId,
        int sessionId,
        SafeKernelHandle process,
        SafeKernelHandle job,
        IDesktopLaunchHandshake handshake)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(handshake);

        ProcessId = processId;
        SessionId = sessionId;
        _process = process;
        _job = job;
        _handshake = handshake;
        _ready = handshake.WaitForHealthyAsync(CancellationToken.None);
        _waitHandle = new ProcessWaitHandle(process);
        try
        {
            _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                _waitHandle,
                static (state, _) =>
                {
                    var completion = (TaskCompletionSource)state!;
                    completion.TrySetResult();
                },
                _exited,
                Timeout.InfiniteTimeSpan,
                executeOnlyOnce: true);
        }
        catch
        {
            _waitHandle.Dispose();
            throw;
        }
    }

    public int ProcessId { get; }

    public int SessionId { get; }

    public Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        return _ready.WaitAsync(cancellationToken);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        return _exited.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = _registeredWait.Unregister(waitObject: null);
        _waitHandle.Dispose();
        _job.Dispose();
        _process.Dispose();
        await _handshake.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the process SafeHandle alive for as long as the managed wait handle may expose its
    /// raw value to the thread-pool wait registration.
    /// </summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        private readonly SafeKernelHandle _process;
        private bool _addedReference;

        public ProcessWaitHandle(SafeKernelHandle process)
        {
            _process = process;
            process.DangerousAddRef(ref _addedReference);
            SafeWaitHandle = new SafeWaitHandle(
                process.DangerousGetHandle(),
                ownsHandle: false);
        }

        protected override void Dispose(bool explicitDisposing)
        {
            base.Dispose(explicitDisposing);
            if (_addedReference)
            {
                _process.DangerousRelease();
                _addedReference = false;
            }
        }
    }
}
