using System.ComponentModel;
using System.Diagnostics;
using Zara.Infrastructure.Windows.Updates;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsUpdateInstallerTests
{
    [TestMethod]
    public async Task RequestsInstallerThroughStaExplorerPathWithoutArguments()
    {
        ProcessStartInfo? actual = null;
        ApartmentState apartment = ApartmentState.Unknown;
        var installer = new WindowsUpdateInstaller(info =>
        {
            actual = info;
            apartment = Thread.CurrentThread.GetApartmentState();
            return true;
        });

        Assert.IsTrue(await installer.LaunchAsync(@"C:\한글 빌드\test & build.exe"));
        Assert.IsNotNull(actual);
        Assert.AreEqual(@"C:\한글 빌드\test & build.exe", actual.FileName);
        Assert.AreEqual(string.Empty, actual.Arguments);
        Assert.AreEqual("runas", actual.Verb);
        Assert.IsTrue(actual.UseShellExecute);
        Assert.AreEqual(ApartmentState.STA, apartment);
    }

    [TestMethod]
    public async Task UacCancellationIsNotReportedAsSuccess()
    {
        var installer = new WindowsUpdateInstaller(_ => throw new Win32Exception(1223));
        Assert.IsFalse(await installer.LaunchAsync(@"C:\build.exe"));
    }
}
