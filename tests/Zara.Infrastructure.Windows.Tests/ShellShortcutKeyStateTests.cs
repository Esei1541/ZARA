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
