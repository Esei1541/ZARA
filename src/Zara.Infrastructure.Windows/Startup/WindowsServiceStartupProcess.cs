using Zara.Application.Startup;
using Zara.Infrastructure.Windows.Continuity;

namespace Zara.Infrastructure.Windows.Startup;

/// <summary>Runs only the fixed, short-lived service start operation before Desktop startup.</summary>
public static class WindowsServiceStartupProcess
{
    private const int WorkerFailureMarker = 0x40000000;

    public static bool TryRunWorker(string[] arguments, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        exitCode = 0;
        if (!arguments.Any(argument => argument.StartsWith(
                WindowsServiceStartupPort.WorkerArgument,
                StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (arguments.Length != 1 ||
            !string.Equals(arguments[0], WindowsServiceStartupPort.WorkerArgument,
                StringComparison.Ordinal))
        {
            exitCode = EncodeWorkerFailure(new DesktopStartupException(
                StartupFailureKind.RegistrationRejected,
                "The ZARA Service worker accepts only its fixed argument."));
            return true;
        }

        try
        {
            string expectedPath = Path.Combine(
                AppContext.BaseDirectory,
                WindowsSupervisionServerVerifier.ServiceExecutableName);
            new WindowsServiceStartupNative(expectedPath).Start();
        }
        catch (Exception exception)
        {
            DesktopStartupException failure =
                WindowsServiceStartupPort.ConvertFailure(exception,
                    "The elevated ZARA Service worker could not start the service.");
            exitCode = EncodeWorkerFailure(failure);
        }

        return true;
    }

    internal static int EncodeWorkerFailure(DesktopStartupException failure) =>
        WorkerFailureMarker | ((int)failure.Kind << 16) |
        (failure.NativeErrorCode.GetValueOrDefault() & 0xffff);

    internal static DesktopStartupException DecodeWorkerFailure(int exitCode)
    {
        if ((exitCode & 0xff000000) != WorkerFailureMarker)
        {
            return new DesktopStartupException(
                StartupFailureKind.StartFailed,
                "The elevated ZARA Service worker exited without a recognized result.",
                exitCode);
        }

        int kindValue = (exitCode >> 16) & 0xff;
        if (!Enum.IsDefined(typeof(StartupFailureKind), kindValue))
        {
            return new DesktopStartupException(
                StartupFailureKind.Unexpected,
                "The elevated ZARA Service worker returned an unknown failure.",
                exitCode);
        }

        int nativeCode = exitCode & 0xffff;
        return new DesktopStartupException(
            (StartupFailureKind)kindValue,
            "The elevated ZARA Service worker could not start the service.",
            nativeCode == 0 ? null : nativeCode);
    }
}
