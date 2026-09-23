using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Zara.Infrastructure.Windows;

/// <summary>Serializes manual startup prompts without reserving the Desktop activation endpoint.</summary>
public sealed class WindowsDesktopStartupGate : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private WindowsDesktopStartupGate(Mutex mutex) => _mutex = mutex;

    /// <summary>Must be acquired and disposed on the entry-point UI thread.</summary>
    public static WindowsDesktopStartupGate? TryAcquire()
    {
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        using Process process = Process.GetCurrentProcess();
        string identity = $"{user.User?.Value}|{process.SessionId}|{Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant()}";
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var mutex = new Mutex(false, $"Local\\ZARA.Desktop.Startup.{key}");
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        if (acquired)
        {
            return new WindowsDesktopStartupGate(mutex);
        }
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
