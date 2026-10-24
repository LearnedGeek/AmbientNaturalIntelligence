namespace AniRuntime.Core.Models;

/// <summary>
/// F-4 Phase 7 (2026-10-23) — one multi-week narrative thread Ani is
/// inside of. Examples: "the shelf I'm building," "the Peru cookbook,"
/// "the conversation I've been meaning to have with Lila."
///
/// <para>
/// Lives on <see cref="CharacterStateDoc.OngoingArcs"/>. Updated at
/// reflection time by <c>IArcExtractor</c> — recent inner thoughts are
/// scanned for arc mentions; a mention of an existing arc bumps
/// <see cref="LastTouchedAt"/>, a mention of a novel one creates a slot.
/// Arcs not touched for the configured stall threshold transition to
/// <see cref="ArcStatus.Stalled"/>; the composer still sees stalled arcs
/// at lower priority but they don't dominate the surface.
/// </para>
///
/// <para>
/// <b>Why not a Dictionary&lt;string, float&gt;:</b> arcs carry temporal
/// identity (started-at, last-touched-at) that a weight-only shape loses.
/// Phase 1/1.1 TopicValence+ToneValence are dicts because topics compete
/// on weight only; arcs compete on recency × specificity, and need a
/// summary slot for the composer to read.
/// </para>
/// </summary>
public sealed class ArcState
{
    /// <summary>Canonical short name — the arc's identity key.</summary>
    public string          Name          { get; set; } = string.Empty;

    /// <summary>When the arc first entered state.</summary>
    public DateTimeOffset  StartedAt     { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Most recent cycle the arc was referenced in inner thought.</summary>
    public DateTimeOffset  LastTouchedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// One-sentence prose the composer sees. Updated each time the arc
    /// is referenced so the summary reflects the latest state Ani holds
    /// about it ("almost done" / "stuck on the ending" / "waiting for her
    /// to text back").
    /// </summary>
    public string          Summary       { get; set; } = string.Empty;

    /// <summary>Lifecycle slot — see <see cref="ArcStatus"/>.</summary>
    public ArcStatus       Status        { get; set; } = ArcStatus.Active;
}

public enum ArcStatus
{
    /// <summary>Still ongoing — Ani references it or expects to resume.</summary>
    Active,

    /// <summary>Not touched for the configured stall threshold; still remembered but demoted.</summary>
    Stalled,

    /// <summary>Explicitly finished — mentioned in a completing frame ("done with", "finished", etc.).</summary>
    Completed,
}
