using System.ComponentModel;
using System.Runtime.InteropServices;
using Zara.Infrastructure.Windows;

namespace Zara.Infrastructure.Windows.Tests;

[TestClass]
public sealed partial class NativeWindowPositionerTests
{
    private static readonly nint PerMonitorAwareV2 = new(-4);

    [TestMethod]
    public void HiddenWindowUsesExactNegativePhysicalPixelBounds()
    {
        using var dpiAwareness = new ThreadDpiAwarenessScope(PerMonitorAwareV2);
        using var window = new HiddenNativeWindow();
        var positioner = new NativeWindowPositioner();
        var expectedBounds = new PixelBounds(x: -1234, y: -567, width: 321, height: 123);

        positioner.PositionTopmostNoActivate(window.WindowHandle, expectedBounds);

        Assert.AreEqual(expectedBounds, positioner.GetWindowBounds(window.WindowHandle));
    }

    private sealed class HiddenNativeWindow : NativeWindow, IDisposable
    {
        private const int WindowStylePopup = unchecked((int)0x80000000);
        private const int WindowExtendedStyleToolWindow = 0x00000080;

        internal HiddenNativeWindow()
        {
            var createParams = new CreateParams
            {
                Caption = $"ZARA Native Position Test {Guid.NewGuid():N}",
                Style = WindowStylePopup,
                ExStyle = WindowExtendedStyleToolWindow,
                X = 0,
                Y = 0,
                Width = 1,
                Height = 1,
                Parent = nint.Zero,
            };
            CreateHandle(createParams);
        }

        internal nint WindowHandle => Handle;

        public void Dispose()
        {
            if (Handle != nint.Zero)
            {
                DestroyHandle();
            }

            GC.SuppressFinalize(this);
        }
    }

    private sealed class ThreadDpiAwarenessScope : IDisposable
    {
        private readonly nint _previousContext;
        private int _disposed;

        internal ThreadDpiAwarenessScope(nint dpiAwarenessContext)
        {
            _previousContext = SetThreadDpiAwarenessContext(dpiAwarenessContext);
            if (_previousContext == nint.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Windows rejected the test thread DPI-awareness context.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (SetThreadDpiAwarenessContext(_previousContext) == nint.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Windows did not restore the test thread DPI-awareness context.");
            }

            GC.SuppressFinalize(this);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetThreadDpiAwarenessContext(nint dpiContext);
}
