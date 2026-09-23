using System.ComponentModel;
using Zara.Application.Startup;
using Zara.Infrastructure.Windows.Startup;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsServiceStartupTests
{
    private static readonly string ExpectedPath = Path.Combine(
        Path.GetTempPath(), "zara-startup-test", "Zara.Enforcement.Service.exe");

    [TestMethod]
    public void StoppedServiceStillRequiresExactRegistration()
    {
        StartupServiceSnapshot valid = Snapshot(state: 1);
        WindowsServiceStartupNative.ValidateSnapshot(valid, ExpectedPath, binaryExists: true);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            WindowsServiceStartupNative.ValidateSnapshot(
                valid with { AccountName = "LocalService" }, ExpectedPath, true));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            WindowsServiceStartupNative.ValidateSnapshot(
                valid with { BinaryPath = ExpectedPath + " --unexpected" }, ExpectedPath, true));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            WindowsServiceStartupNative.ValidateSnapshot(
                valid with { ConfiguredServiceType = 0x20 }, ExpectedPath, true));
    }

    [TestMethod]
    public void MissingRegisteredBinaryAndDisabledStatusAreDistinct()
    {
        DesktopStartupException missing = Assert.ThrowsExactly<DesktopStartupException>(() =>
            WindowsServiceStartupNative.ValidateSnapshot(Snapshot(1), ExpectedPath, false));
        Assert.AreEqual(StartupFailureKind.BinaryMissing, missing.Kind);

        ServiceStartupStatus status = WindowsServiceStartupNative.MapSnapshot(
            Snapshot(1) with
            {
                StartType = 4,
                Win32ExitCode = 1066,
                ServiceExitCode = 23
            });
        Assert.AreEqual(StartupServiceState.Stopped, status.State);
        Assert.IsTrue(status.StartDisabled);
        Assert.AreEqual(1066u, status.Win32ExitCode);
        Assert.AreEqual(23u, status.ServiceExitCode);
    }

    [TestMethod]
    public void ServiceHandlesUseOnlyNeededRightsAndConcurrentStartIsAccepted()
    {
        Assert.AreEqual(0x5u, WindowsServiceStartupNative.RequestedAccess(false));
        Assert.AreEqual(0x15u, WindowsServiceStartupNative.RequestedAccess(true));
        Assert.IsTrue(WindowsServiceStartupNative.IsConcurrentAlreadyRunning(1056));
        Assert.IsFalse(WindowsServiceStartupNative.IsConcurrentAlreadyRunning(1058));
    }

    [TestMethod]
    public void NativeErrorsKeepSpecificFailureAndCode()
    {
        var cases = new (int Code, StartupFailureKind Kind)[]
        {
            (5, StartupFailureKind.AccessDenied),
            (1060, StartupFailureKind.ServiceMissing),
            (1072, StartupFailureKind.ServiceDeleting),
            (1058, StartupFailureKind.ServiceDisabled),
            (2, StartupFailureKind.BinaryMissing),
            (1223, StartupFailureKind.ElevationCancelled)
        };
        foreach ((int code, StartupFailureKind kind) in cases)
        {
            DesktopStartupException failure = WindowsServiceStartupPort.ConvertFailure(
                new Win32Exception(code), "test");
            Assert.AreEqual(kind, failure.Kind);
            Assert.AreEqual(code, failure.NativeErrorCode);
        }
    }

    [TestMethod]
    public void WorkerRejectsExtraOrAlteredArgumentsWithoutStartingService()
    {
        Assert.IsTrue(WindowsServiceStartupProcess.TryRunWorker(
            ["--start-zara-service", "another-target"], out int extraCode));
        Assert.AreEqual(StartupFailureKind.RegistrationRejected,
            WindowsServiceStartupProcess.DecodeWorkerFailure(extraCode).Kind);
        Assert.IsTrue(WindowsServiceStartupProcess.TryRunWorker(
            ["--START-ZARA-SERVICE"], out int alteredCode));
        Assert.AreEqual(StartupFailureKind.RegistrationRejected,
            WindowsServiceStartupProcess.DecodeWorkerFailure(alteredCode).Kind);
        Assert.IsTrue(WindowsServiceStartupProcess.TryRunWorker(
            ["--start-zara-service=other"], out int suffixedCode));
        Assert.AreEqual(StartupFailureKind.RegistrationRejected,
            WindowsServiceStartupProcess.DecodeWorkerFailure(suffixedCode).Kind);
        Assert.IsFalse(WindowsServiceStartupProcess.TryRunWorker(
            ["--something-else"], out _));
    }

    [TestMethod]
    public void ElevatedLaunchHasOnlyFixedExecutableArgumentAndRunas()
    {
        var info = WindowsServiceStartupElevator.CreateStartInfo(
            Path.Combine(Path.GetTempPath(), "Zara.Desktop.exe"));
        Assert.AreEqual(WindowsServiceStartupPort.WorkerArgument, info.Arguments);
        Assert.AreEqual("runas", info.Verb);
        Assert.IsTrue(info.UseShellExecute);
        Assert.AreEqual("Zara.Desktop.exe", Path.GetFileName(info.FileName));
    }

    [TestMethod]
    public async Task StartSelectionUsesOnlyRequestedPathAndChecksCancellation()
    {
        var native = new FakeNative();
        var elevator = new FakeElevator();
        var port = new WindowsServiceStartupPort(native, elevator);

        await port.StartAsync(false, CancellationToken.None);
        Assert.AreEqual(1, native.Starts);
        Assert.AreEqual(0, elevator.Runs);

        await port.StartAsync(true, CancellationToken.None);
        Assert.AreEqual(1, native.Starts);
        Assert.AreEqual(1, elevator.Runs);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            port.StartAsync(false, cancelled.Token));
        Assert.AreEqual(1, native.Starts);
    }

    [TestMethod]
    public async Task ElevatedWorkerFailurePreservesKindAndNativeCode()
    {
        var native = new FakeNative();
        var elevator = new FakeElevator
        {
            ExitCode = WindowsServiceStartupProcess.EncodeWorkerFailure(
                new DesktopStartupException(StartupFailureKind.ServiceDeleting,
                    "deleting", 1072))
        };
        var port = new WindowsServiceStartupPort(native, elevator);
        DesktopStartupException failure = await Assert.ThrowsExactlyAsync<DesktopStartupException>(
            () => port.StartAsync(true, CancellationToken.None));
        Assert.AreEqual(StartupFailureKind.ServiceDeleting, failure.Kind);
        Assert.AreEqual(1072, failure.NativeErrorCode);
        Assert.AreEqual(0, native.Starts);
    }

    private static StartupServiceSnapshot Snapshot(uint state) => new(
        State: state,
        ServiceType: 0x10,
        Win32ExitCode: 0,
        ServiceExitCode: 0,
        ConfiguredServiceType: 0x10,
        StartType: 3,
        AccountName: "LocalSystem",
        BinaryPath: ExpectedPath);

    private sealed class FakeNative : IServiceStartupNative
    {
        internal int Starts;
        public ServiceStartupStatus ReadStatus() => new(StartupServiceState.Stopped);
        public void Start() => Interlocked.Increment(ref Starts);
    }

    private sealed class FakeElevator : IServiceStartupElevator
    {
        internal int Runs;
        internal int ExitCode;
        public Task<int> RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            return Task.FromResult(ExitCode);
        }
    }
}
