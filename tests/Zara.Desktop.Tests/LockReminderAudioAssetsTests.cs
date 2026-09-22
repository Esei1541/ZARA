using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace Zara.Desktop.Tests;

[TestClass]
public sealed class LockReminderAudioAssetsTests
{
    [TestMethod]
    [DataRow(30, "439F624D4064737EBD53862BED42E6B676DE5F56767459EC56E63F962960E455")]
    [DataRow(10, "19FEB8E70D2A391A87D7900087C831D6697C0C70F25AD839063CD1CD5F3AA2BE")]
    [DataRow(5, "98CC7DCB6C9B1A81A37141A4883A775B92F1FF8CB581ADB3BDD7AD8C7F366422")]
    [DataRow(1, "33A8B8ECB0AF95621EF765457F942D3180E3E3387574F20E6B1D6AD6D320F7D7")]
    public void OutputContainsTheProvidedAudioForEveryReminder(int minutes, string expectedHash)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "Assets", "Audio",
            minutes.ToString(CultureInfo.InvariantCulture) + "min.mp3");

        Assert.IsTrue(File.Exists(path), $"Missing reminder clip: {path}");
        using FileStream stream = File.OpenRead(path);
        Assert.AreEqual(expectedHash, Convert.ToHexString(SHA256.HashData(stream)));
    }
}
