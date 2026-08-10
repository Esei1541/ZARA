using Microsoft.Win32.SafeHandles;

namespace Zara.Enforcement.Service.Windows;

/// <summary>
/// Owns one CloseHandle-compatible native object returned by the supervision host.
/// </summary>
internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeKernelHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeKernelHandle(nint preexistingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle()
    {
        return NativeMethods.CloseHandle(handle) != 0;
    }
}
