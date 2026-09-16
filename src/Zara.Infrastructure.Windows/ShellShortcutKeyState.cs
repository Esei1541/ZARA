namespace Zara.Infrastructure.Windows;

/// <summary>
/// Tracks only the key pairs suppressed by the shell-shortcut hook; never stores typed text.
/// Used exclusively by the hook thread.
/// </summary>
internal sealed class ShellShortcutKeyState
{
    private const uint Tab = 0x09;
    private const uint Escape = 0x1B;
    private const uint Space = 0x20;
    private const uint LeftWindows = 0x5B;
    private const uint RightWindows = 0x5C;
    private const uint F4 = 0x73;
    private readonly bool[] _suppressed = new bool[256];
    private bool _leftWindowsDown;
    private bool _rightWindowsDown;

    internal void Reset(bool leftWindowsDown = false, bool rightWindowsDown = false)
    {
        Array.Clear(_suppressed);
        _leftWindowsDown = leftWindowsDown;
        _rightWindowsDown = rightWindowsDown;
    }

    internal bool ShouldSuppress(
        uint virtualKey,
        bool keyUp,
        bool controlDown,
        bool altDown = false,
        bool shiftDown = false)
    {
        if (virtualKey == LeftWindows)
        {
            _leftWindowsDown = !keyUp;
            return true;
        }

        if (virtualKey == RightWindows)
        {
            _rightWindowsDown = !keyUp;
            return true;
        }

        if (virtualKey >= _suppressed.Length)
        {
            return false;
        }

        if (keyUp)
        {
            bool suppressed = _suppressed[virtualKey];
            _suppressed[virtualKey] = false;
            return suppressed;
        }

        // Let modifier pairs reach Windows, including modifiers held before locking.
        // Ctrl+Alt+Del and Ctrl+Shift+Esc remain available for the external recovery path.
        if (virtualKey is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5)
        {
            return false;
        }

        bool shellShortcut =
            _leftWindowsDown || _rightWindowsDown ||
            (altDown && virtualKey is Tab or Escape or Space or F4) ||
            (controlDown && !shiftDown && virtualKey == Escape);

        // Suppression survives repeats and modifier release until this key's matching key-up.
        _suppressed[virtualKey] |= shellShortcut;
        return _suppressed[virtualKey];
    }
}
