using System.Globalization;
using System.Text;

namespace Zara.Application.UsagePolicy;

/// <summary>
/// Describes how one displayed text segment relates to the emergency-unlock input.
/// </summary>
public enum EmergencyUnlockInputState
{
    /// <summary>The expected text has not been entered yet.</summary>
    Pending,

    /// <summary>The entered text matches the expected text at the same position.</summary>
    Matched,

    /// <summary>The entered text does not match the expected text at the same position.</summary>
    Mismatched,
}

/// <summary>
/// Groups adjacent Unicode text elements that share one emergency-unlock input state.
/// </summary>
/// <param name="Text">
/// The entered text for matched or mismatched positions, or the expected text for positions that
/// have not been entered.
/// </param>
/// <param name="State">How <paramref name="Text" /> relates to the expected challenge.</param>
public sealed record EmergencyUnlockInputSegment(
    string Text,
    EmergencyUnlockInputState State);

/// <summary>
/// Contains the display-ready progress for one emergency-unlock input and its exact-match result.
/// </summary>
/// <param name="Segments">Adjacent text segments grouped by their match state.</param>
/// <param name="IsExactMatch">
/// Whether the entered text exactly matches the challenge after normalizing line endings.
/// </param>
public sealed record EmergencyUnlockInputComparison(
    IReadOnlyList<EmergencyUnlockInputSegment> Segments,
    bool IsExactMatch);

/// <summary>
/// Compares emergency-unlock input with one challenge without depending on WPF or other I/O.
/// </summary>
/// <remarks>
/// Comparison uses Unicode text elements so surrogate pairs and combining sequences are not split
/// for display. Exact completion remains ordinal and only treats CRLF, CR, and LF line endings as
/// equivalent.
/// </remarks>
public sealed class EmergencyUnlockInputMatcher
{
    private readonly string _expectedText;
    private readonly string[] _expectedTextElements;

    /// <summary>
    /// Creates a matcher and caches the expected Unicode text elements for repeated input updates.
    /// </summary>
    /// <param name="challenge">The challenge shown for the current emergency-unlock request.</param>
    public EmergencyUnlockInputMatcher(EmergencyUnlockChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        _expectedText = challenge.ExpectedText;
        _expectedTextElements = GetTextElements(_expectedText);
    }

    /// <summary>
    /// Builds display segments for the current input while preserving entered mismatched text.
    /// </summary>
    /// <param name="enteredText">The current text from the emergency-unlock input control.</param>
    /// <returns>
    /// Match-state segments in display order and whether the complete input exactly matches.
    /// </returns>
    public EmergencyUnlockInputComparison Compare(string enteredText)
    {
        ArgumentNullException.ThrowIfNull(enteredText);

        string normalizedEnteredText = NormalizeLineEndings(enteredText);
        string[] enteredTextElements = GetTextElements(normalizedEnteredText);
        int comparisonLength = Math.Max(_expectedTextElements.Length, enteredTextElements.Length);
        var segments = new List<EmergencyUnlockInputSegment>();
        var segmentText = new StringBuilder();
        EmergencyUnlockInputState? segmentState = null;

        for (int index = 0; index < comparisonLength; index++)
        {
            (string text, EmergencyUnlockInputState state) = GetPosition(
                index,
                enteredTextElements);
            if (segmentState is not null && segmentState != state)
            {
                segments.Add(new EmergencyUnlockInputSegment(segmentText.ToString(), segmentState.Value));
                segmentText.Clear();
            }

            segmentState = state;
            segmentText.Append(text);
        }

        if (segmentState is not null)
        {
            segments.Add(new EmergencyUnlockInputSegment(segmentText.ToString(), segmentState.Value));
        }

        return new EmergencyUnlockInputComparison(
            segments.ToArray(),
            string.Equals(normalizedEnteredText, _expectedText, StringComparison.Ordinal));
    }

    /// <summary>
    /// Determines whether entered text exactly completes a challenge using the same line-ending
    /// rule as <see cref="Compare(string)" />.
    /// </summary>
    /// <param name="challenge">The pending emergency-unlock challenge.</param>
    /// <param name="enteredText">The text submitted by the user.</param>
    /// <returns><see langword="true" /> only when the normalized input matches exactly.</returns>
    public static bool IsExactMatch(
        EmergencyUnlockChallenge challenge,
        string enteredText)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(enteredText);
        return string.Equals(
            NormalizeLineEndings(enteredText),
            challenge.ExpectedText,
            StringComparison.Ordinal);
    }

    private (string Text, EmergencyUnlockInputState State) GetPosition(
        int index,
        string[] enteredTextElements)
    {
        if (index >= enteredTextElements.Length)
        {
            return (_expectedTextElements[index], EmergencyUnlockInputState.Pending);
        }

        string enteredText = enteredTextElements[index];
        bool matched = index < _expectedTextElements.Length &&
            string.Equals(enteredText, _expectedTextElements[index], StringComparison.Ordinal);
        return (
            enteredText,
            matched ? EmergencyUnlockInputState.Matched : EmergencyUnlockInputState.Mismatched);
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string[] GetTextElements(string value)
    {
        var elements = new List<string>();
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        return elements.ToArray();
    }
}
