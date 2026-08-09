using System.Collections.Immutable;
using Microsoft.Win32;
using FormsScreen = System.Windows.Forms.Screen;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Enumerates attached Windows displays and relays system display-setting notifications.
/// </summary>
/// <remarks>
/// Notifications are raised on the thread used by <see cref="SystemEvents" />. A UI consumer must
/// marshal the callback to its dispatcher before touching thread-affine presentation objects.
/// </remarks>
public sealed class WindowsDisplayTopology : IDisposable
{
    private int _disposeState;

    /// <summary>
    /// Initializes the topology source and subscribes to Windows display-setting notifications.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// System event notifications are not supported in the current process context.
    /// </exception>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">
    /// Windows could not create the system-events notification thread.
    /// </exception>
    public WindowsDisplayTopology()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>
    /// Occurs after Windows reports that display settings have changed.
    /// </summary>
    /// <remarks>
    /// The event is a change signal rather than a cached topology. Call <see cref="Capture" /> after
    /// marshalling to the desired thread to obtain a fresh immutable snapshot.
    /// </remarks>
    public event EventHandler? TopologyChanged;

    /// <summary>
    /// Captures all currently attached displays using their full physical pixel bounds.
    /// </summary>
    /// <returns>
    /// An immutable array containing one snapshot per entry returned by
    /// <see cref="FormsScreen.AllScreens" />.
    /// </returns>
    /// <exception cref="ObjectDisposedException">This topology source has been disposed.</exception>
    public ImmutableArray<DisplaySnapshot> Capture()
    {
        ThrowIfDisposed();

        FormsScreen[] screens = FormsScreen.AllScreens;
        var snapshots = ImmutableArray.CreateBuilder<DisplaySnapshot>(screens.Length);

        foreach (FormsScreen screen in screens)
        {
            System.Drawing.Rectangle bounds = screen.Bounds;
            snapshots.Add(
                new DisplaySnapshot(
                    screen.DeviceName,
                    new PixelBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                    screen.Primary));
        }

        return snapshots.MoveToImmutable();
    }

    /// <summary>
    /// Unsubscribes from Windows display-setting notifications.
    /// </summary>
    /// <remarks>Repeated calls are safe and have no additional effect.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        TopologyChanged = null;
        GC.SuppressFinalize(this);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        TopologyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }
}
