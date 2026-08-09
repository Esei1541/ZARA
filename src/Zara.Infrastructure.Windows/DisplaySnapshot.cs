namespace Zara.Infrastructure.Windows;

/// <summary>
/// Describes a rectangular region in physical desktop pixels.
/// </summary>
public readonly record struct PixelBounds
{
    /// <summary>
    /// Initializes physical pixel bounds whose size is strictly positive.
    /// </summary>
    /// <param name="x">The horizontal coordinate in the virtual desktop pixel space.</param>
    /// <param name="y">The vertical coordinate in the virtual desktop pixel space.</param>
    /// <param name="width">The width in physical pixels.</param>
    /// <param name="height">The height in physical pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width" /> or <paramref name="height" /> is not positive.
    /// </exception>
    public PixelBounds(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Gets the horizontal coordinate in the virtual desktop pixel space.
    /// </summary>
    public int X { get; }

    /// <summary>
    /// Gets the vertical coordinate in the virtual desktop pixel space.
    /// </summary>
    public int Y { get; }

    /// <summary>
    /// Gets the width in physical pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the height in physical pixels.
    /// </summary>
    public int Height { get; }

    internal static void Validate(PixelBounds bounds, string parameterName)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                bounds,
                "Physical pixel bounds must have a positive width and height.");
        }
    }
}

/// <summary>
/// Captures the identity and physical pixel bounds of an attached display at one point in time.
/// </summary>
public sealed record DisplaySnapshot
{
    /// <summary>
    /// Initializes an immutable display snapshot.
    /// </summary>
    /// <param name="deviceName">The Windows display device name used to correlate later snapshots.</param>
    /// <param name="pixelBounds">The full display bounds in physical virtual-desktop pixels.</param>
    /// <param name="isPrimary">Whether Windows currently identifies the display as primary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceName" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="deviceName" /> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pixelBounds" /> does not have a positive width and height.
    /// </exception>
    public DisplaySnapshot(string deviceName, PixelBounds pixelBounds, bool isPrimary)
    {
        ArgumentNullException.ThrowIfNull(deviceName);

        if (string.IsNullOrWhiteSpace(deviceName))
        {
            throw new ArgumentException("A display device name is required.", nameof(deviceName));
        }

        PixelBounds.Validate(pixelBounds, nameof(pixelBounds));

        DeviceName = deviceName;
        PixelBounds = pixelBounds;
        IsPrimary = isPrimary;
    }

    /// <summary>
    /// Gets the Windows display device name used to correlate later snapshots.
    /// </summary>
    public string DeviceName { get; }

    /// <summary>
    /// Gets the full display bounds in physical virtual-desktop pixels.
    /// </summary>
    public PixelBounds PixelBounds { get; }

    /// <summary>
    /// Gets a value indicating whether Windows currently identifies the display as primary.
    /// </summary>
    public bool IsPrimary { get; }
}
