using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Zara.Infrastructure.Windows;

/// <summary>
/// Positions native windows without exposing the underlying Win32 call to presentation code.
/// </summary>
public interface INativeWindowPositioner
{
    /// <summary>
    /// Places a window at physical pixel bounds in the topmost band without activating it.
    /// </summary>
    /// <param name="windowHandle">A valid HWND owned by the caller.</param>
    /// <param name="pixelBounds">The desired outer-window bounds.</param>
    void PositionTopmostNoActivate(nint windowHandle, PixelBounds pixelBounds);
}

/// <summary>
/// Positions an existing native window in physical desktop pixels without activating it.
/// </summary>
/// <remarks>
/// The caller must configure the process for per-monitor-v2 DPI awareness and invoke the method in
/// the lifecycle appropriate for the window that owns the supplied handle.
/// </remarks>
public sealed partial class NativeWindowPositioner : INativeWindowPositioner
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly nint HwndTopmost = new(-1);

    /// <summary>
    /// Initializes a native window positioner.
    /// </summary>
    public NativeWindowPositioner()
    {
    }

    /// <summary>
    /// Places a window at the exact physical pixel bounds and moves it into the topmost band without
    /// activating it or changing its owner ordering.
    /// </summary>
    /// <param name="windowHandle">A valid HWND owned by the caller.</param>
    /// <param name="pixelBounds">The desired outer-window bounds in physical virtual-desktop pixels.</param>
    /// <exception cref="ArgumentException"><paramref name="windowHandle" /> is zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pixelBounds" /> does not have a positive width and height.
    /// </exception>
    /// <exception cref="Win32Exception">Windows rejected the positioning request.</exception>
    public void PositionTopmostNoActivate(nint windowHandle, PixelBounds pixelBounds)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A non-zero window handle is required.", nameof(windowHandle));
        }

        PixelBounds.Validate(pixelBounds, nameof(pixelBounds));

        int succeeded = SetWindowPos(
            windowHandle,
            HwndTopmost,
            pixelBounds.X,
            pixelBounds.Y,
            pixelBounds.Width,
            pixelBounds.Height,
            SwpNoActivate | SwpNoOwnerZOrder);

        if (succeeded == 0)
        {
            int errorCode = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                errorCode,
                $"SetWindowPos failed for window handle 0x{windowHandle:X}.");
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowPos(
        nint windowHandle,
        nint windowInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
