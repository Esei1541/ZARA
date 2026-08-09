namespace Zara.Core.Runtime;

/// <summary>
/// Contains the state and declarative external effects produced by one reducer event.
/// </summary>
/// <param name="NextState">The state after processing the event.</param>
/// <param name="Effects">The ordered external effects to execute.</param>
public sealed record RuntimeTransition(
    RuntimeState NextState,
    IReadOnlyList<RuntimeEffect> Effects);
