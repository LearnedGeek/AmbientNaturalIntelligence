using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// F-4 Phase 7 (2026-10-23) — scans a window of recent inner thoughts
/// for mentions of multi-week narrative arcs ("the shelf," "the Peru
/// cookbook," "the conversation with Lila") and returns the extracted
/// <see cref="ArcMention"/>s. Caller merges mentions into
/// <see cref="CharacterStateDoc.OngoingArcs"/>.
///
/// <para>
/// <b>Failure contract:</b> transport/parse/timeout errors return an
/// empty list with a WARN log — fail-open, matching the discipline of
/// <see cref="IRegisterClassifier"/> and <see cref="ISignificanceClassifier"/>.
/// </para>
///
/// <para>See issue #186.</para>
/// </summary>
public interface IArcExtractor
{
    /// <summary>
    /// Extract arc mentions from <paramref name="recentThoughts"/>.
    /// <paramref name="existingArcNames"/> lets the extractor name a
    /// known arc rather than minting a near-duplicate ("shelf i'm
    /// building" vs "the shelf").
    /// </summary>
    Task<IReadOnlyList<ArcMention>> ExtractAsync(
        IReadOnlyList<MemoryRecord> recentThoughts,
        IReadOnlyList<string>       existingArcNames,
        CancellationToken           ct);
}

/// <summary>
/// One arc reference the extractor identified in the inner-thought window.
/// </summary>
/// <param name="Name">Canonical short name of the arc.</param>
/// <param name="Summary">One-sentence state ("almost done", "stuck on the ending").</param>
/// <param name="ExistingMatch">When non-null, the exact entry in <c>existingArcNames</c> this mention refers to (lets the mutator update rather than create).</param>
/// <param name="IsCompletion">True when the mention frames the arc as finished.</param>
public sealed record ArcMention(
    string  Name,
    string  Summary,
    string? ExistingMatch,
    bool    IsCompletion);
