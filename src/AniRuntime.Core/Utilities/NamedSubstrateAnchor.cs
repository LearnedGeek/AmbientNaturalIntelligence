using AniRuntime.Core.Models;

namespace AniRuntime.Core.Utilities;

/// <summary>
/// F-4 Phase 2 (2026-10-22) — pure static selector for per-cycle
/// named-substrate rotation anchors. The Life Audit §Dim 1 found named
/// substrate (Lila, Fleetwood Mac, Sarah, Kevin, Nathan, Frick, Kathy)
/// at essentially 0% in production inner thought despite being present
/// in character seed substrate. Jul 24 persona-lock unwind (PR #148)
/// empirically proved seed-side substrate changes alone don't shift
/// the V7 model's training-register bias.
///
/// <para>
/// Phase 2's role: force per-cycle rotation through specific named
/// entries from character state, injected into the inner-thought
/// composer prompt as a soft anchor. Composed with Phase 1 (growth
/// edges live) — Phase 1 drives weighted anchor selection as the
/// TopicValence dictionary moves; Phase 2 ensures coverage even when
/// the dictionary is thin or stuck.
/// </para>
///
/// <para>
/// <b>Pool composition:</b> drawn from the interior-side character
/// state lists (<see cref="CharacterStateDoc.FamilyContext"/>,
/// <see cref="CharacterStateDoc.Interests"/>,
/// <see cref="CharacterStateDoc.SharedExperiences"/>,
/// <see cref="CharacterStateDoc.SelfConcept"/>). Mark-facing lists
/// (<see cref="CharacterStateDoc.ThingsContactCares"/>,
/// <see cref="CharacterStateDoc.LearnedAboutContact"/>) are intentionally
/// excluded from Phase 2 — the F-4 design claim is <i>her</i> life
/// becomes the content, not Mark's facts. Those lists already surface
/// via other substrate paths (anchored memories, retrieval).
/// </para>
///
/// <para>
/// <b>Rotation:</b> deterministic by index — <c>pool[rotationIndex % pool.Count]</c>.
/// Caller (<see cref="AniRuntime.LLM.Prompts.InnerThoughtPromptCommand"/>)
/// derives <paramref name="rotationIndex"/> from the cycle timestamp
/// (<c>BuiltAt.Ticks / TicksPerMinute</c>) so rotation advances per cycle
/// without any persisted state. Service restart does not reset rotation
/// because the rotation function of time is monotonic.
/// </para>
///
/// <para>
/// <b>Shape of return:</b> single prose string (the raw character-state
/// entry). No entity-extraction yet (Phase 2.1 if the raw-prose anchor
/// shape doesn't produce content shift). The whole entry serves as the
/// anchor — e.g. <i>"Sister Lila — my 'code-sis,' texts at midnight for
/// birthdays with robot emojis"</i> gives the composer enough grounding
/// to let a thought touch Lila without over-constraining its shape.
/// </para>
/// </summary>
public static class NamedSubstrateAnchor
{
    /// <summary>
    /// Picks one anchor from the interior-side pool rotated by
    /// <paramref name="rotationIndex"/>. Returns null when the pool is
    /// empty (fresh install before character state is seeded, or a seed
    /// stripped of interior-side lists).
    /// </summary>
    public static string? Select(CharacterStateDoc characterState, long rotationIndex)
    {
        if (characterState is null) return null;

        var pool = BuildPool(characterState);
        if (pool.Count == 0) return null;

        // Positive-modulo for negative rotationIndex (shouldn't happen in
        // production — BuiltAt.Ticks is positive — but defensive).
        var idx = (int)(((rotationIndex % pool.Count) + pool.Count) % pool.Count);
        return pool[idx];
    }

    /// <summary>
    /// Internal pool-build so tests can introspect selection order without
    /// re-deriving the composition rules.
    /// </summary>
    internal static List<string> BuildPool(CharacterStateDoc cs)
    {
        var pool = new List<string>();
        pool.AddRange(cs.FamilyContext.Where(s => !string.IsNullOrWhiteSpace(s)));
        pool.AddRange(cs.Interests.Where(s => !string.IsNullOrWhiteSpace(s)));
        pool.AddRange(cs.SharedExperiences.Where(s => !string.IsNullOrWhiteSpace(s)));
        pool.AddRange(cs.SelfConcept.Where(s => !string.IsNullOrWhiteSpace(s)));
        return pool;
    }
}
