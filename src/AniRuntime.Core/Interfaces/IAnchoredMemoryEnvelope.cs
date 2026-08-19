using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Voice classification for anchored (foundation) memories. Introduced
/// 2026-08-19 for F-1 Phase 7 (<see cref="IAnchoredMemoryEnvelope"/>) so
/// the inner-thought composer can split the pre-Phase-7 single-heading
/// AnchoredMemories dump into three purpose-appropriate groups instead of
/// mixing "who Ani is" with "facts about Mark" with "background world
/// narrative" in one bucket.
///
/// <para>
/// Motivating failure — issue #63 (recurring bookstore attribution loop):
/// pronoun-flip of Mark-facts into Ani-identity statements happened
/// because the model saw everything under the same "part of who you are"
/// heading. Splitting by voice at the render boundary makes the
/// distinction legible without a schema change.
/// </para>
/// </summary>
public enum AnchoredMemoryVoice
{
    /// <summary>Fallback — voice couldn't be classified from content signals.</summary>
    Unclassified = 0,

    /// <summary>
    /// Ani-self-statement — Character seeds addressed to Ani ("You work
    /// at...", "You're 34..."), Interior-tier inner-thought anchors, or
    /// content using first-person self-reference. Renders under
    /// <c>"Part of who you are:"</c>.
    /// </summary>
    AniSelfStatement = 1,

    /// <summary>
    /// Mark-fact-assertion — Facts anchored about Mark as subject
    /// ("Mark teaches at WCTC", "Mark's family lives in..."). Renders
    /// under <c>"Things you know about Mark:"</c>.
    /// </summary>
    MarkFactAssertion = 2,

    /// <summary>
    /// Seed-narrative — Background world content Ani can draw from that
    /// is neither self-address nor Mark-attribution. World-seed facts
    /// about the setting, generic knowledge, atmospheric context.
    /// Renders under <c>"Background you can draw from:"</c>.
    /// </summary>
    SeedNarrative = 3,
}

/// <summary>
/// Foundation Input (F-1) Phase 7 (2026-08-19) — producer-boundary envelope
/// for anchored (foundation) memories. Adds a derived <see cref="Voice"/>
/// classification so the inner-thought composer can split the anchored
/// pool by rendering-target-heading rather than dumping everything under
/// one bucket where the model conflates "who Ani is" with "what Ani knows
/// about Mark" with "background world narrative."
///
/// <para>
/// <see cref="MemoryRecord"/> implements this envelope alongside
/// <see cref="IRetrievalEnvelope"/> (Phase 5) — one class, two envelope
/// interfaces that share the same <c>IProvenancedContent&lt;MemoryRecord&gt;</c>
/// base implementation. Voice is DERIVED at read time from
/// <see cref="MemoryRecord.Provenance"/> + content pronoun signals — no
/// schema change, no LLM call, no backfill (Phase 7 discipline: minimum
/// data-model change; a Phase 7b classifier can replace the heuristic
/// if empirical results demand it).
/// </para>
///
/// <para>
/// Empirical anchor: issue #63 recurring bookstore-attribution loop —
/// pronoun-flip of Mark-facts into Ani-identity statements traced to
/// mixed rendering under one "part of who you are" heading.
/// </para>
/// </summary>
public interface IAnchoredMemoryEnvelope : IProvenancedContent<MemoryRecord>
{
    /// <summary>Derived voice classification driving rendering-heading split.</summary>
    AnchoredMemoryVoice Voice { get; }
}
