namespace Zara.Application.UsagePolicy;

/// <summary>
/// Supplies distinct practice sentences for one product emergency-unlock request.
/// </summary>
public interface IEmergencyUnlockPromptCatalog
{
    /// <summary>
    /// Selects the requested number of distinct valid sentences.
    /// </summary>
    /// <param name="count">The number of sentences required for this request.</param>
    /// <param name="cancellationToken">Cancels loading or selecting prompts.</param>
    /// <returns>Exactly <paramref name="count"/> distinct sentences.</returns>
    Task<IReadOnlyList<string>> SelectDistinctAsync(
        int count,
        CancellationToken cancellationToken = default);
}
