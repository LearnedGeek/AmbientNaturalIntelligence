namespace AniRuntime.Core.Models;

/// <summary>
/// F-4 Phase 5a (2026-10-24) — temporal grounding substrate for the
/// inner-thought composer. Closes the "reporting-from-inside a thinking
/// mode vs responding to a turn" trap that the May 19 Cognitive-Mode-
/// Awareness stub named: when Ani's chat-tuned base model sees a prior-
/// cycle gist, it treats the gist content as the current scene rather
/// than as a recalled prior thought.
///
/// Architectural precedent: Park et al 2023 "Generative Agents" §4.1 —
/// every memory record carries a creation timestamp and the reflection
/// prompt feeds in the N most-recent records in natural language so the
/// agent reasons over the temporal sequence rather than confabulating.
/// ANI already stores both <see cref="MemoryRecord.OccurredAt"/> and
/// <see cref="MemoryRecord.CreatedAt"/>; Phase 5a wires those timestamps
/// through to the composer with explicit "prior cycles, not now" framing.
///
/// <para>Related: issue #184.</para>
/// </summary>
public sealed record TemporalContext(
    double?                         HoursSinceLastInnerThought,
    IReadOnlyList<RecentInnerThought> RecentInnerThoughts);

/// <summary>
/// One prior inner-thought surface with its persisted timestamp, so the
/// composer can distinguish "I thought this 3 hours ago" from "this is
/// what's happening now." Grounded in the actual record, not re-derived
/// from a gist's compression.
/// </summary>
public sealed record RecentInnerThought(
    string         Content,
    DateTimeOffset OccurredAt);
