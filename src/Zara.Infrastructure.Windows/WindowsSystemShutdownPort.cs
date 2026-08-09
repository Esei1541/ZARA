using System.ComponentModel;
using System.Diagnostics;
using Zara.Application.SystemPower;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Requests a normal Windows shutdown through the operating system's inbox shutdown executable.
/// </summary>
/// <remarks>
/// Completion confirms that <c>shutdown.exe</c> accepted the request and exited successfully; it
/// does not wait for Windows to finish shutting down. Once the executable starts, the operation is
/// no longer cancelable because the shutdown request may already have been accepted.
/// </remarks>
public sealed class WindowsSystemShutdownPort : ISystemShutdownPort
{
    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">
    /// Cancellation was requested before the shutdown process started.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The Windows system root is unavailable, the process could not be started, or the shutdown
    /// request process returned a nonzero exit code.
    /// </exception>
    /// <exception cref="Win32Exception">Windows could not start the shutdown executable.</exception>
    public async Task RequestShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string shutdownExecutablePath = GetShutdownExecutablePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = shutdownExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/t");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("p:0:0");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Windows did not start the shutdown request process '{shutdownExecutablePath}'.");
        }

        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        string standardError = (await standardErrorTask.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            string errorDetail = string.IsNullOrEmpty(standardError)
                ? "No error details were returned."
                : standardError;
            throw new InvalidOperationException(
                $"Windows shutdown request failed with exit code {process.ExitCode}. {errorDetail}");
        }
    }

    private static string GetShutdownExecutablePath()
    {
        string systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(systemDirectory) ||
            !Path.IsPathFullyQualified(systemDirectory))
        {
            throw new InvalidOperationException(
                "Windows did not provide an absolute system directory path.");
        }

        return Path.Combine(systemDirectory, "shutdown.exe");
    }
}
