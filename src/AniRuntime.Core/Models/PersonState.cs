namespace AniRuntime.Core.Models;

/// <summary>
/// F-4 Phase 6 (2026-11-02) — accrued state about one non-Mark canonical
/// person in Ani's world (Sister Lila, Dominus, bookstore regulars, etc.).
///
/// <para>
/// Lives on <see cref="CharacterStateDoc.KnownPeople"/> keyed by canonical
/// name. Updated at reflection time by <c>IPersonTropeExtractor</c> —
/// recent inner thoughts are scanned for mentions of canonical-cast people
/// and distilled into short general <see cref="SharedTropes"/> (patterns,
/// not events). The confabulation discipline: fabricated events never land
/// here; only extracted patterns about canonical-cast people do.
/// </para>
///
/// <para>
/// <b>Why tropes, not events:</b> Phase 6 writes to canonical substrate on
/// named-person keys. Storing specific "Lila did X yesterday" style events
/// would conflict with Paper 1's "substrate reflects lived experience"
/// claim — Ani never actually witnessed those events. A trope like "Lila
/// cares about her students" is a generalization already implicit in the
/// seed and the thoughts that touched her, which is honest substrate.
/// </para>
///
/// <para>See issue #185.</para>
/// </summary>
public sealed class PersonState
{
    /// <summary>Canonical name — the slot's identity key.</summary>
    public string          Name             { get; set; } = string.Empty;

    /// <summary>
    /// Bounded list of short general patterns about this person. Most-
    /// recent first. The reflection-time mutator caps the list so one
    /// person's slot never dominates the composer surface.
    /// </summary>
    public List<string>    SharedTropes     { get; set; } = new();

    /// <summary>When this person first appeared in extracted tropes.</summary>
    public DateTimeOffset  FirstMentionedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Most recent reflection cycle that touched this slot.</summary>
    public DateTimeOffset  LastTouchedAt    { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Count of reflection cycles that found a trope for this person.
    /// Not the number of inner-thought references — a single reflection
    /// run bumps this once even if several thoughts in the window named
    /// the person.
    /// </summary>
    public int             MentionCount     { get; set; }
}
