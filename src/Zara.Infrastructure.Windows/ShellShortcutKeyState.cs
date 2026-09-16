namespace Zara.Infrastructure.Windows;

/// <summary>
/// Tracks only the key pairs suppressed by the shell-shortcut hook; never stores typed text.
/// Used exclusively by the hook thread.
/// </summary>
internal sealed class ShellShortcutKeyState
{
    private const uint Escape = 0x1B;
    private const uint LeftWindows = 0x5B;
    private const uint RightWindows = 0x5C;
    private bool _escapeSuppressed;

    internal bool ShouldSuppress(uint virtualKey, bool keyUp, bool controlDown)
    {
        if (virtualKey is LeftWindows or RightWindows)
        {
            return true;
        }

        if (virtualKey != Escape)
        {
            return false;
        }

        if (keyUp)
        {
            bool suppressed = _escapeSuppressed;
            _escapeSuppressed = false;
            return suppressed;
        }

        // Keep repeats and the matching key-up suppressed even when Ctrl is released first.
        _escapeSuppressed |= controlDown;
        return _escapeSuppressed;
    }
}
