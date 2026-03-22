namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Extracts semantic intent from natural language messages.
/// Used to improve memory retrieval by searching with the message's
/// meaning rather than its surface tokens. Prevents retrieval contamination
/// where casual greetings or shared vocabulary pull in irrelevant memories.
/// </summary>
public interface IIntentExtractor
{
    /// <summary>
    /// Extracts the core topic/intent of a message in a short phrase.
    /// E.g., "Thanks yeah I decided to just relax tonight. No work."
    ///     → "relaxing at home, taking the evening off"
    /// </summary>
    Task<string> ExtractIntentAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Scores how relevant an article/item summary is to a set of interest descriptions.
    /// Returns 0.0 (irrelevant) to 1.0 (highly relevant).
    /// Uses semantic understanding, not keyword matching.
    /// </summary>
    Task<float> ScoreRelevanceAsync(string itemSummary, IReadOnlyList<string> interestDescriptions, CancellationToken ct = default);
}
