using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Builds the emotional-state portion of <see cref="ContextSnapshot"/>:
/// relationship health, emotional drift, pattern awareness, processed themes.
///
/// Extracted 2026-05-19 as the first sub-builder of the ContextBuilder SRP
/// decomposition (`ANI-Testability-Architecture-Plan.md` §2). Production impl:
/// <c>EmotionalContextBuilder</c> in <c>AniRuntime.Loops.Context</c>.
/// </summary>
public interface IEmotionalContextBuilder
{
    /// <summary>
    /// Build the emotional-context block. Reads recent message volume,
    /// conversation valence, emotional history, and outreach patterns;
    /// recalculates and persists <see cref="RelationshipHealth"/> when stale
    /// (>24h since last calculation).
    /// </summary>
    /// <param name="characterName">
    /// Character name used for pattern-awareness phrasing only (logged, not
    /// queried). Pass <c>CharacterStateDoc.Name</c>.
    /// </param>
    /// <param name="emotionalState">
    /// Current emotional state. Used when relationship-health is recalculated
    /// so the new score reflects the current warmth/connection vector.
    /// </param>
    Task<EmotionalContextResult> BuildAsync(
        string              characterName,
        EmotionalState      emotionalState,
        CancellationToken   ct);
}
