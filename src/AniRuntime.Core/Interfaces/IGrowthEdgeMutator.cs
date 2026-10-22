using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// F-4 Phase 1 (2026-10-22) — runtime mutator for the character-state
/// "growth edges" (<see cref="CharacterStateDoc.TopicValence"/> and
/// <see cref="CharacterStateDoc.ToneValence"/>). The Life Audit §Dim 4
/// found these dictionaries identical to seed values after 7+ months of
/// production runtime because no code path mutates them. The architecture
/// for an evolving life assumes they do.
///
/// <para>
/// Phase 1.0 scope: TopicValence only. ToneValence mutation deferred to
/// Phase 1.1 (requires mapping Mark-reply register-classifier output to
/// tone descriptors; non-trivial seam).
/// </para>
///
/// <para>
/// <b>Dual mandate</b> (Phase 0 decision 2026-10-22): this mutator serves
/// two coupled purposes. (1) Make growth edges evolve. (2) Produce the
/// feedback loop that lets dictionary mutation drive content — the
/// <see cref="CharacterStateDoc.TopicValence"/> top-K is read back by
/// the inner-thought composer each cycle as a rotation anchor. Without the
/// reader half, the writer half is still inert. Without the writer half,
/// the reader half can only read seed values.
/// </para>
///
/// <para>
/// <b>Bounded delta discipline:</b> per-cycle updates are capped
/// (<see cref="AniRuntime.Core.AniOptions.GrowthEdgeMaxDeltaPerCycle"/>) so
/// the dictionary evolves gradually. Keys below
/// <see cref="AniRuntime.Core.AniOptions.GrowthEdgePruneThreshold"/> are
/// optionally pruned so the dictionary doesn't accumulate noise topics.
/// New keys enter at <see cref="AniRuntime.Core.AniOptions.GrowthEdgeNewKeyBaseWeight"/>
/// × extracted salience, so a strong first signal lands higher than a weak
/// one but never maxes out on first observation.
/// </para>
///
/// <para>
/// <b>Failure contract:</b> transport / parse / timeout exceptions leave
/// the dictionary unchanged and return a result with
/// <see cref="GrowthEdgeUpdateResult.KeysUpdated"/> = 0. The caller
/// (<c>ReflectionPhase</c>) logs via <see cref="GrowthEdgeUpdateResult"/>
/// but does not fail the cycle. This matches the fail-open contract
/// established by <see cref="IRegisterClassifier"/>.
/// </para>
///
/// <para>
/// See <c>ani-docs/spec/ANI-Foundation-Life-Refactor-Plan.md</c> §Phase 1
/// for the design claim and the acceptance criteria. Primary empirical
/// anchor: <c>ani-docs/research/ANI-Life-Audit-2026-10-22.md</c>.
/// </para>
/// </summary>
public interface IGrowthEdgeMutator
{
    /// <summary>
    /// Extracts topic-salience signals from <paramref name="recentThoughts"/>
    /// (typically the records just reflected-upon by
    /// <see cref="AniRuntime.Loops.ReflectionPhase"/>, so the extraction
    /// aligns with the same window the reflection synthesis already produced
    /// a gist from) and applies bounded deltas to
    /// <paramref name="characterState"/>'s <see cref="CharacterStateDoc.TopicValence"/>
    /// in-place.
    /// </summary>
    /// <returns>
    /// A <see cref="GrowthEdgeUpdateResult"/> summarizing what changed. On
    /// transport/parse failure returns an empty result (no exception). The
    /// caller is responsible for persisting the mutated character state —
    /// this method does NOT call <c>SaveCharacterStateAsync</c>, so writes
    /// are atomic with whatever other character-state mutations the caller
    /// is making this cycle.
    /// </returns>
    Task<GrowthEdgeUpdateResult> UpdateTopicValenceAsync(
        CharacterStateDoc characterState,
        IReadOnlyList<MemoryRecord> recentThoughts,
        CancellationToken ct);
}

/// <summary>
/// Return shape for <see cref="IGrowthEdgeMutator.UpdateTopicValenceAsync"/>.
/// Fields are intended for both telemetry (<c>F4_GROWTH_EDGE_UPDATE</c>
/// structured log) and audit — the caller logs the result on every cycle
/// so the Phase 1 acceptance check (dictionary evolves, ≥3 non-seed keys
/// after 30 days) can be grepped out of journal logs.
/// </summary>
public sealed record GrowthEdgeUpdateResult(
    int KeysUpdated,
    int NewKeysAdded,
    int KeysPruned,
    IReadOnlyList<(string Key, float Delta)> Changes)
{
    public static GrowthEdgeUpdateResult Empty { get; } =
        new(0, 0, 0, Array.Empty<(string, float)>());
}
