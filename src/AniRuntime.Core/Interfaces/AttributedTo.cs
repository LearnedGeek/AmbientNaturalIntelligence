namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P1 (2026-08-21) — who authored
/// the content within a record. First-class attribution metadata, orthogonal
/// to <see cref="IProvenancedContent{T}"/>'s producer/tier information.
///
/// <para>
/// <b>Narrow enum by design</b> (per plan doc D1): Ani has one contact today
/// (Mark). Contact-other and Composite are deliberately excluded to avoid
/// premature abstraction. Adding new values later is additive and non-breaking
/// for existing records because the underlying storage is <c>INTEGER</c>-backed
/// with numeric values pinned below.
/// </para>
///
/// <para>
/// <b>Zero-value semantics:</b> <see cref="Unknown"/> is <c>0</c> so
/// uninitialized fields default to Unknown rather than silently attributing
/// to Mark or Ani. Every record must explicitly set this at ingest time —
/// the Phase 3 backfill uses a heuristic table (tier + type + SourceName) to
/// populate for existing records; if inference fails, the record stays
/// <see cref="Unknown"/> and lands on the manual-curation tail.
/// </para>
/// </summary>
public enum AttributedTo
{
    /// <summary>Author not classified — default at record construction.</summary>
    Unknown = 0,

    /// <summary>The primary contact (Mark) uttered the content.</summary>
    Mark = 1,

    /// <summary>Ani authored the content (interior thought, outreach, reply, reflection).</summary>
    Ani = 2,

    /// <summary>Non-utterance external world event (RSS, weather, time-of-day).</summary>
    World = 3,
}
