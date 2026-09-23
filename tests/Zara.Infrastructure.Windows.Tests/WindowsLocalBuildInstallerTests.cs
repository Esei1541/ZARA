#if LOCAL_BUILD_UPDATES
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Zara.Infrastructure.Windows.LocalBuilds;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsLocalBuildInstallerTests
{
    [TestMethod]
    public async Task InstallerUsesElevationWithoutShellCommandInterpolation()
    {
        ProcessStartInfo? actual = null;
        var installer = new WindowsLocalBuildInstaller(info =>
        {
            actual = info;
            return true;
        });
        const string path = @"C:\한글 빌드\test & build.exe";

        Assert.IsTrue(await installer.LaunchAsync(path));
        Assert.IsNotNull(actual);
        Assert.AreEqual(path, actual.FileName);
        Assert.IsTrue(actual.UseShellExecute);
        Assert.AreEqual("runas", actual.Verb);
        Assert.AreEqual(string.Empty, actual.Arguments);
    }

    [TestMethod]
    public async Task ElevationCancellationIsNotReportedAsSuccess()
    {
        var installer = new WindowsLocalBuildInstaller(_ => throw new Win32Exception(1223));
        Assert.IsFalse(await installer.LaunchAsync(@"C:\build.exe"));
    }

    [TestMethod]
    public async Task LaunchFailurePropagatesToTheCaller()
    {
        var installer = new WindowsLocalBuildInstaller(_ => throw new Win32Exception(2));
        await Assert.ThrowsExactlyAsync<Win32Exception>(() => installer.LaunchAsync(@"C:\build.exe"));
    }

    [TestMethod]
    public async Task ShellRequestRunsInAnStaApartment()
    {
        ApartmentState? apartment = null;
        var installer = new WindowsLocalBuildInstaller(_ =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            return true;
        });

        Assert.IsTrue(await installer.LaunchAsync(@"C:\build.exe"));
        Assert.AreEqual(ApartmentState.STA, apartment);
    }

    [TestMethod]
    public async Task ReportedComElevationCancellationIsNotReportedAsSuccess()
    {
        var installer = new WindowsLocalBuildInstaller(
            _ => throw Marshal.GetExceptionForHR(unchecked((int)0x800704C7), new IntPtr(-1))!);

        Assert.IsFalse(await installer.LaunchAsync(@"C:\build.exe"));
    }

    [TestMethod]
    public async Task ExplorerFailureReachesTheCaller()
    {
        var failure = Marshal.GetExceptionForHR(unchecked((int)0x80010108), new IntPtr(-1))!;
        var installer = new WindowsLocalBuildInstaller(_ => throw failure);

        var actual = await Assert.ThrowsExactlyAsync<COMException>(
            () => installer.LaunchAsync(@"C:\build.exe"));
        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    public async Task CanceledRequestNeverOpensElevation()
    {
        int calls = 0;
        var installer = new WindowsLocalBuildInstaller(_ =>
        {
            calls++;
            return true;
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => installer.LaunchAsync(@"C:\build.exe", cancellation.Token));
        Assert.AreEqual(0, calls);
    }
}
#endif
