using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Pumps WH_KEYBOARD_LL on a dedicated thread. The callback does no UI work, waiting, or logging.
/// </summary>
internal sealed partial class WindowsShellShortcutHook : IShellShortcutHook
{
    private const int WhKeyboardLl = 13;
    private const uint WmQuit = 0x0012;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;
    private const uint LlkhfAltDown = 0x20;
    private const uint EventSystemDesktopSwitch = 0x0020;
    private const uint WinEventOutOfContext = 0;

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ShellShortcutKeyState _keys = new();
    private readonly Thread _thread;
    private readonly HookProcedure _callback;
    private readonly WinEventProcedure _desktopSwitchCallback;
    private uint _threadId;
    private int _enabled;
    private int _finished;

    internal WindowsShellShortcutHook()
    {
        _callback = HookCallback;
        _desktopSwitchCallback = OnDesktopSwitch;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ZARA lock shortcuts",
        };
    }

    public Task Started => _started.Task;

    public Task Stopped => _stopped.Task;

    public void Start() => _thread.Start();

    public void Stop()
    {
        // Input becomes pass-through immediately, even if posting the quit message fails.
        Volatile.Write(ref _enabled, 0);
        if (Volatile.Read(ref _finished) != 0)
        {
            return;
        }

        if (PostThreadMessageW(_threadId, WmQuit, 0, 0) == 0 &&
            Volatile.Read(ref _finished) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not stop the keyboard hook thread.");
        }
    }

    private void Run()
    {
        SafeHookHandle? hook = null;
        SafeWinEventHookHandle? desktopSwitchHook = null;
        Exception? failure = null;
        try
        {
            // Create the message queue before publishing readiness to the caller.
            _ = PeekMessageW(out _, 0, 0, 0, 0);
            _threadId = GetCurrentThreadId();
            nint module = GetModuleHandleW(null);
            if (module == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not identify the hook module.");
            }

            nint desktopSwitchHandle = SetWinEventHook(
                EventSystemDesktopSwitch, EventSystemDesktopSwitch, 0,
                Marshal.GetFunctionPointerForDelegate(_desktopSwitchCallback), 0, 0, WinEventOutOfContext);
            if (desktopSwitchHandle == 0)
            {
                throw new InvalidOperationException("Could not observe desktop switches for the keyboard hook.");
            }

            desktopSwitchHook = new SafeWinEventHookHandle(desktopSwitchHandle);
            nint handle = SetWindowsHookExW(
                WhKeyboardLl, Marshal.GetFunctionPointerForDelegate(_callback), module, 0);
            if (handle == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not install the keyboard hook.");
            }

            hook = new SafeHookHandle(handle);
            ResetKeyState();
            Volatile.Write(ref _enabled, 1);
            _started.TrySetResult();

            while (true)
            {
                int result = GetMessageW(out NativeMessage message, 0, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result == -1)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Keyboard hook message loop failed.");
                }

                _ = TranslateMessage(in message);
                _ = DispatchMessageW(in message);
            }
        }
#pragma warning disable CA1031 // Report native/thread failures through the awaited lifecycle tasks.
        catch (Exception exception)
        {
            failure = exception;
        }
#pragma warning restore CA1031
        finally
        {
            Volatile.Write(ref _enabled, 0);
            hook?.Dispose();
            if (hook is { ReleaseError: not 0 })
            {
                failure ??= new Win32Exception(hook.ReleaseError, "Could not remove the keyboard hook.");
            }

            desktopSwitchHook?.Dispose();
            if (desktopSwitchHook is { ReleaseFailed: true })
            {
                failure ??= new InvalidOperationException("Could not remove the desktop-switch event hook.");
            }

            // Keep the native callback rooted until unhooking on its owning thread is complete.
            GC.KeepAlive(_callback);
            GC.KeepAlive(_desktopSwitchCallback);
            Volatile.Write(ref _finished, 1);
            if (failure is null)
            {
                _stopped.TrySetResult();
            }
            else
            {
                bool startupFailed = _started.TrySetException(failure);
                if (startupFailed && hook is null && desktopSwitchHook is not { ReleaseFailed: true })
                {
                    // Installation failed without acquiring a hook; there is nothing to restore.
                    _stopped.TrySetResult();
                }
                else
                {
                    _stopped.TrySetException(failure);
                }
            }
        }
    }

    private unsafe nint HookCallback(int code, nuint message, nint data)
    {
        if (code == 0 && Volatile.Read(ref _enabled) != 0 &&
            message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
        {
            KeyboardData keyboard = *(KeyboardData*)data;
            uint virtualKey = keyboard.VirtualKey;
            bool keyUp = message is WmKeyUp or WmSysKeyUp;
            bool controlDown = virtualKey == 0x1B && !keyUp &&
                (GetAsyncKeyState(VkControl) & 0x8000) != 0;
            bool shiftDown = controlDown && (GetAsyncKeyState(VkShift) & 0x8000) != 0;
            bool altDown = (keyboard.Flags & LlkhfAltDown) != 0;
            if (_keys.ShouldSuppress(virtualKey, keyUp, controlDown, altDown, shiftDown))
            {
                return 1;
            }
        }

        return CallNextHookEx(0, code, message, data);
    }

    private void OnDesktopSwitch(
        nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (eventType == EventSystemDesktopSwitch)
        {
            // Out-of-context WinEvents run on this same message-loop thread. Key releases on
            // the secure desktop are invisible to our keyboard hook, so discard all old pairs.
            ResetKeyState();
        }
    }

    private void ResetKeyState() => _keys.Reset(
        leftWindowsDown: (GetAsyncKeyState(VkLeftWindows) & 0x8000) != 0,
        rightWindowsDown: (GetAsyncKeyState(VkRightWindows) & 0x8000) != 0);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProcedure(
        nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint eventTime);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProcedure(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        internal uint VirtualKey;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;
    }

    private sealed class SafeWinEventHookHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeWinEventHookHandle(nint hook) : base(ownsHandle: true) => SetHandle(hook);

        internal bool ReleaseFailed { get; private set; }

        protected override bool ReleaseHandle()
        {
            ReleaseFailed = UnhookWinEvent(handle) == 0;
            return !ReleaseFailed;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(
        uint eventMin, uint eventMax, nint module, nint callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int UnhookWinEvent(nint hook);

    private sealed class SafeHookHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeHookHandle(nint hook) : base(ownsHandle: true) => SetHandle(hook);

        internal int ReleaseError { get; private set; }

        protected override bool ReleaseHandle()
        {
            if (UnhookWindowsHookEx(handle) != 0)
            {
                return true;
            }

            int error = Marshal.GetLastPInvokeError();
            // Windows may already have removed a timed-out hook (ERROR_INVALID_HOOK_HANDLE).
            ReleaseError = error == 1404 ? 0 : error;
            return ReleaseError == 0;
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int hookType, nint callback, nint module, uint threadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(out NativeMessage message, nint window, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial int PeekMessageW(out NativeMessage message, nint window, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(in NativeMessage message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in NativeMessage message);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint GetModuleHandleW(string? moduleName);
}
