namespace Zara.Core.Runtime;

/// <summary>
/// Represents an external operation declared by the pure runtime reducer.
/// </summary>
public abstract record RuntimeEffect;

/// <summary>
/// Requests that an adapter reconcile all lock overlays to the specified visibility.
/// </summary>
/// <param name="Visibility">The visibility that the adapter must establish.</param>
public sealed record ApplyOverlayVisibility(
    OverlayVisibility Visibility) : RuntimeEffect;
