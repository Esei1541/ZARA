using System.Text;
using Zara.Application.UsagePolicy;

namespace Zara.Infrastructure.Windows.UsagePolicy;

/// <summary>
/// Loads the packaged emergency-unlock prompt presets from the application's base directory.
/// </summary>
/// <remarks>
/// The catalog validates every packaged line before it becomes available. A missing or malformed
/// preset therefore fails the request instead of silently reducing the challenge set. Selection
/// uses a partial Fisher-Yates shuffle so no prompt can occur twice in one challenge.
/// </remarks>
public sealed class WindowsEmergencyPromptCatalog : IEmergencyUnlockPromptCatalog
{
    private const string PresetsDirectoryName = "Presets";
    private const int MinimumPromptLength = 20;
    private const int MaximumPromptLength = 90;
    private const int MinimumHangulCharacterCount = 12;
    private static readonly string[] RequiredPresetFileNames =
    [
        "gwangyeom-sonata.txt",
        "readymade-life.txt",
        "wings.txt",
    ];
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _presetsDirectoryPath;
    private readonly Random _random;
    private readonly object _randomGate = new();

    /// <summary>
    /// Initializes a catalog that reads the <c>Presets</c> directory beside the running application.
    /// </summary>
    public WindowsEmergencyPromptCatalog()
        : this(Path.Combine(AppContext.BaseDirectory, PresetsDirectoryName), Random.Shared)
    {
    }

    /// <summary>
    /// Initializes a catalog rooted at a supplied preset directory.
    /// </summary>
    /// <remarks>
    /// This constructor is internal so Infrastructure tests can use an isolated directory without
    /// changing the process-wide application base directory.
    /// </remarks>
    /// <param name="presetsDirectoryPath">The absolute directory containing every required preset.</param>
    /// <param name="random">The random source used for non-repeating selection.</param>
    internal WindowsEmergencyPromptCatalog(string presetsDirectoryPath, Random random)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetsDirectoryPath);
        ArgumentNullException.ThrowIfNull(random);
        if (!Path.IsPathFullyQualified(presetsDirectoryPath))
        {
            throw new ArgumentException(
                "An absolute preset directory path is required.",
                nameof(presetsDirectoryPath));
        }

        _presetsDirectoryPath = Path.GetFullPath(presetsDirectoryPath);
        _random = random;
    }

    /// <summary>
    /// Returns a random set of distinct prompts from all packaged preset files.
    /// </summary>
    /// <param name="count">The number of prompts required for the emergency-unlock challenge.</param>
    /// <param name="cancellationToken">Cancels file reading, validation, or selection.</param>
    /// <returns>Exactly <paramref name="count" /> distinct, validated prompts.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative.</exception>
    /// <exception cref="FileNotFoundException">A required packaged preset file is missing.</exception>
    /// <exception cref="InvalidDataException">
    /// A preset file is not valid UTF-8 or contains an invalid prompt.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The validated prompt set does not contain enough distinct prompts for the request.
    /// </exception>
    public async Task<IReadOnlyList<string>> SelectDistinctAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        cancellationToken.ThrowIfCancellationRequested();
        if (count == 0)
        {
            return Array.Empty<string>();
        }

        List<string> prompts = await LoadValidatedPromptsAsync(cancellationToken).ConfigureAwait(false);
        if (count > prompts.Count)
        {
            throw new InvalidOperationException(
                $"The packaged prompt catalog contains {prompts.Count} distinct prompts, " +
                $"but {count} prompts were requested.");
        }

        lock (_randomGate)
        {
            for (int selectedIndex = 0; selectedIndex < count; selectedIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int candidateIndex = _random.Next(selectedIndex, prompts.Count);
                (prompts[selectedIndex], prompts[candidateIndex]) =
                    (prompts[candidateIndex], prompts[selectedIndex]);
            }
        }

        return prompts.GetRange(0, count);
    }

    private async Task<List<string>> LoadValidatedPromptsAsync(CancellationToken cancellationToken)
    {
        var prompts = new List<string>();
        var knownPrompts = new HashSet<string>(StringComparer.Ordinal);

        foreach (string presetFileName in RequiredPresetFileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string presetFilePath = Path.Combine(_presetsDirectoryPath, presetFileName);
            if (!File.Exists(presetFilePath))
            {
                throw new FileNotFoundException(
                    $"The required emergency-unlock preset file '{presetFileName}' is missing.",
                    presetFilePath);
            }

            string[] lines = await ReadPresetLinesAsync(presetFilePath, cancellationToken)
                .ConfigureAwait(false);
            if (lines.Length == 0)
            {
                throw new InvalidDataException(
                    $"The emergency-unlock preset file '{presetFileName}' contains no prompts.");
            }

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string prompt = lines[lineIndex];
                if (!IsValidPrompt(prompt))
                {
                    throw new InvalidDataException(
                        $"The emergency-unlock preset file '{presetFileName}' contains an invalid " +
                        $"prompt at line {lineIndex + 1}.");
                }

                if (knownPrompts.Add(prompt))
                {
                    prompts.Add(prompt);
                }
            }
        }

        return prompts;
    }

    private static async Task<string[]> ReadPresetLinesAsync(
        string presetFilePath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllLinesAsync(presetFilePath, StrictUtf8, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"The emergency-unlock preset file '{Path.GetFileName(presetFilePath)}' is not valid UTF-8.",
                exception);
        }
    }

    private static bool IsValidPrompt(string prompt)
    {
        if (prompt.Length is < MinimumPromptLength or > MaximumPromptLength ||
            !IsSentenceTerminator(prompt[^1]))
        {
            return false;
        }

        int hangulCharacterCount = 0;
        bool previousCharacterWasTerminator = false;
        foreach (char character in prompt)
        {
            if (character is >= '\uAC00' and <= '\uD7A3')
            {
                hangulCharacterCount++;
                previousCharacterWasTerminator = false;
                continue;
            }

            if (character is >= '0' and <= '9' or ' ' or ',')
            {
                previousCharacterWasTerminator = false;
                continue;
            }

            if (IsSentenceTerminator(character))
            {
                if (previousCharacterWasTerminator)
                {
                    return false;
                }

                previousCharacterWasTerminator = true;
                continue;
            }

            return false;
        }

        return hangulCharacterCount >= MinimumHangulCharacterCount;
    }

    private static bool IsSentenceTerminator(char character) =>
        character is '.' or '!' or '?';
}
