namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class ShellShortcutKeyStateTests
{
    [TestMethod]
    [DataRow(0x5Bu)]
    [DataRow(0x5Cu)]
    public void BothWindowsKeysSuppressDownRepeatAndUp(uint key)
    {
        var state = new ShellShortcutKeyState();

        Assert.IsTrue(state.ShouldSuppress(key, keyUp: false, controlDown: false));
        Assert.IsTrue(state.ShouldSuppress(key, keyUp: false, controlDown: false));
        Assert.IsTrue(state.ShouldSuppress(key, keyUp: true, controlDown: false));
    }

    [TestMethod]
    public void ControlEscapeRemainsSuppressedUntilEscapeIsReleased()
    {
        var state = new ShellShortcutKeyState();

        Assert.IsTrue(state.ShouldSuppress(0x1B, keyUp: false, controlDown: true));
        Assert.IsTrue(state.ShouldSuppress(0x1B, keyUp: false, controlDown: false));
        Assert.IsTrue(state.ShouldSuppress(0x1B, keyUp: true, controlDown: false));
        Assert.IsFalse(state.ShouldSuppress(0x1B, keyUp: false, controlDown: false));
        Assert.IsFalse(state.ShouldSuppress(0x1B, keyUp: true, controlDown: false));
    }

    [TestMethod]
    public void EscapeReleaseWithoutSuppressedPressIsPassedThroughEvenWithControlHeld()
    {
        var state = new ShellShortcutKeyState();

        Assert.IsFalse(state.ShouldSuppress(0x1B, keyUp: false, controlDown: false));
        Assert.IsFalse(state.ShouldSuppress(0x1B, keyUp: true, controlDown: true));
    }

    [TestMethod]
    [DataRow(0x09u, false, false)] // Alt+Tab
    [DataRow(0x09u, false, true)]  // Alt+Shift+Tab
    [DataRow(0x09u, true, false)]  // Ctrl+Alt+Tab
    [DataRow(0x1Bu, false, false)] // Alt+Esc
    [DataRow(0x1Bu, false, true)]  // Alt+Shift+Esc
    [DataRow(0x20u, false, false)] // Alt+Space
    [DataRow(0x73u, false, false)] // Alt+F4
    public void WindowSwitchCloseAndMenuShortcutsKeepTheirKeyPairSuppressed(
        uint key, bool controlDown, bool shiftDown)
    {
        var state = new ShellShortcutKeyState();

        Assert.IsTrue(state.ShouldSuppress(key, false, controlDown, altDown: true, shiftDown));
        Assert.IsTrue(state.ShouldSuppress(key, false, controlDown: false));
        Assert.IsTrue(state.ShouldSuppress(key, true, controlDown: false));
        Assert.IsFalse(state.ShouldSuppress(key, false, controlDown: false));
        Assert.IsFalse(state.ShouldSuppress(key, true, controlDown: false));
    }

    [TestMethod]
    [DataRow(0x09u)] // Win+Tab
    [DataRow(0x44u)] // Win+D / Win+Ctrl+D
    [DataRow(0x4Du)] // Win+M
    [DataRow(0x52u)] // Win+R
    [DataRow(0x53u)] // Win+S
    [DataRow(0x58u)] // Win+X
    [DataRow(0x25u)] // Win+Ctrl+Left
    [DataRow(0x27u)] // Win+Ctrl+Right
    [DataRow(0x73u)] // Win+Ctrl+F4
    public void WindowsChordsSuppressTheActionKeyEvenAfterWindowsIsReleased(uint key)
    {
        foreach (uint windowsKey in new[] { 0x5Bu, 0x5Cu })
        {
            var state = new ShellShortcutKeyState();
            Assert.IsTrue(state.ShouldSuppress(windowsKey, false, false));
            Assert.IsTrue(state.ShouldSuppress(key, false, false));
            Assert.IsTrue(state.ShouldSuppress(windowsKey, true, false));
            Assert.IsTrue(state.ShouldSuppress(key, false, false));
            Assert.IsTrue(state.ShouldSuppress(key, true, false));
            Assert.IsFalse(state.ShouldSuppress(key, false, false));
            Assert.IsFalse(state.ShouldSuppress(key, true, false));
        }
    }

    [TestMethod]
    public void WindowsKeyHeldBeforeHookInstallationStillSuppressesShellActions()
    {
        var state = new ShellShortcutKeyState();

        state.Reset(leftWindowsDown: true);
        Assert.IsTrue(state.ShouldSuppress(0x52, false, false));
        Assert.IsTrue(state.ShouldSuppress(0x5B, true, false));
        Assert.IsTrue(state.ShouldSuppress(0x52, true, false));
        Assert.IsFalse(state.ShouldSuppress(0x52, false, false));
    }

    [TestMethod]
    public void ReleasingOneWindowsKeyDoesNotReleaseTheOther()
    {
        var state = new ShellShortcutKeyState();

        Assert.IsTrue(state.ShouldSuppress(0x5B, false, false));
        Assert.IsTrue(state.ShouldSuppress(0x5C, false, false));
        Assert.IsTrue(state.ShouldSuppress(0x5B, true, false));
        Assert.IsTrue(state.ShouldSuppress(0x44, false, false));
        Assert.IsTrue(state.ShouldSuppress(0x44, true, false));
        Assert.IsTrue(state.ShouldSuppress(0x5C, true, false));
        Assert.IsFalse(state.ShouldSuppress(0x44, false, false));
    }

    [TestMethod]
    public void OverlappingSuppressedKeysHaveIndependentReleaseState()
    {
        var state = new ShellShortcutKeyState();

        Assert.IsTrue(state.ShouldSuppress(0x09, false, false, altDown: true));
        Assert.IsTrue(state.ShouldSuppress(0x1B, false, true));
        Assert.IsTrue(state.ShouldSuppress(0x09, true, false));
        Assert.IsFalse(state.ShouldSuppress(0x09, false, false));
        Assert.IsTrue(state.ShouldSuppress(0x1B, false, false));
        Assert.IsTrue(state.ShouldSuppress(0x1B, true, false));
    }

    [TestMethod]
    public void TaskManagerShortcutAndSecureAttentionKeysRemainAvailable()
    {
        var state = new ShellShortcutKeyState();

        Assert.IsFalse(state.ShouldSuppress(0x1B, false, true, shiftDown: true));
        Assert.IsFalse(state.ShouldSuppress(0x1B, true, true, shiftDown: true));
        Assert.IsFalse(state.ShouldSuppress(0x2E, false, true, altDown: true));
        Assert.IsFalse(state.ShouldSuppress(0x2E, true, true, altDown: true));
    }

    [TestMethod]
    public void ModifierPairsRemainAvailableWhileWindowsIsHeld()
    {
        var state = new ShellShortcutKeyState();
        Assert.IsTrue(state.ShouldSuppress(0x5B, false, false));

        foreach (uint key in new[] { 0x10u, 0x11u, 0x12u, 0xA0u, 0xA1u, 0xA2u, 0xA3u, 0xA4u, 0xA5u })
        {
            Assert.IsFalse(state.ShouldSuppress(key, false, false));
            Assert.IsFalse(state.ShouldSuppress(key, true, false));
        }
    }

    [TestMethod]
    [DataRow(0x09u)]
    [DataRow(0x20u)]
    [DataRow(0x73u)]
    public void KeyReleaseWithoutSuppressedPressIsNotSwallowedByNewModifiers(uint key)
    {
        var state = new ShellShortcutKeyState();

        Assert.IsFalse(state.ShouldSuppress(key, false, false));
        Assert.IsFalse(state.ShouldSuppress(key, true, false, altDown: true));
    }

    [TestMethod]
    public void DesktopSwitchClearsLostWindowsAndActionKeyReleasesBeforeEmergencyTyping()
    {
        var state = new ShellShortcutKeyState();
        Assert.IsTrue(state.ShouldSuppress(0x5B, false, false));
        Assert.IsTrue(state.ShouldSuppress(0x5C, false, false));
        Assert.IsTrue(state.ShouldSuppress(0x41, false, false));
        Assert.IsTrue(state.ShouldSuppress(0x1B, false, true));

        // These keys are released on the secure desktop, where this hook cannot see key-up.
        state.Reset();

        Assert.IsFalse(state.ShouldSuppress(0x41, false, false));
        Assert.IsFalse(state.ShouldSuppress(0x41, true, false));
        Assert.IsFalse(state.ShouldSuppress(0x1B, false, true, shiftDown: true));
        Assert.IsFalse(state.ShouldSuppress(0x1B, true, true, shiftDown: true));
        Assert.IsTrue(state.ShouldSuppress(0x09, false, false, altDown: true));
    }

    [TestMethod]
    public void DesktopSwitchRefreshesHeldWindowsKeysAndDropsThePreviousSnapshot()
    {
        var state = new ShellShortcutKeyState();
        state.Reset(leftWindowsDown: true);
        Assert.IsTrue(state.ShouldSuppress(0x44, false, false));

        state.Reset(rightWindowsDown: true);
        Assert.IsTrue(state.ShouldSuppress(0x5C, true, false));
        Assert.IsFalse(state.ShouldSuppress(0x44, false, false));
        Assert.IsFalse(state.ShouldSuppress(0x44, true, false));
    }

    [TestMethod]
    public void EmergencyUnlockTypingAndAllOtherKeysRemainAvailable()
    {
        var state = new ShellShortcutKeyState();
        for (uint key = 0; key < 256; key++)
        {
            if (key is 0x5B or 0x5C or 0x1B)
            {
                continue;
            }

            Assert.IsFalse(state.ShouldSuppress(key, keyUp: false, controlDown: false), $"Key {key:X}");
            Assert.IsFalse(state.ShouldSuppress(key, keyUp: true, controlDown: false), $"Key {key:X}");
            Assert.IsFalse(state.ShouldSuppress(key, keyUp: false, controlDown: true), $"Ctrl key {key:X}");
            Assert.IsFalse(state.ShouldSuppress(key, keyUp: true, controlDown: true), $"Ctrl key {key:X}");
        }
    }
}
