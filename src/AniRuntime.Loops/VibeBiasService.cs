using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Vibe Loop V1.5 (May 2, 2026) — retrieval-time biasing service.
/// Computes importance-weighted decay-adjusted bias contributions over
/// recent <see cref="ClosedConversationRecord"/> instances per the
/// V1.5.0 design (locked May 2 11:24 CDT).
///
/// Internal helpers (<c>ComputeImportance</c>, <c>DetermineTier</c>,
/// <c>ComputeDecay</c>, <c>ComputeBiasWeight</c>, <c>ApplyMmrRerank</c>,
/// <c>ToOrderedVector</c>) are exposed via <c>InternalsVisibleTo</c> for
/// direct unit testing — pure functions are easier to verify at the
/// helper level than through a full <c>ComputeBiasAsync</c> round-trip.
///
/// **Self-regulation framing (V1.5.0 Lever 3):** the outcome valence
/// driving the bias comes from <c>record.OutcomeSignalValence</c> which
/// V1.2 computes from Ani's register delta (start-of-thread to
/// end-of-thread). Mark's register delta is observable at telemetry
/// rendering but is NEVER read by this service. This is the designed
/// asymmetry that prevents the loop from collapsing into Mark-state
/// optimization (the manipulation / mirroring failure mode).
/// </summary>
public class VibeBiasService : IVibeBiasService
{
    private readonly IClosedConversationStore     _store;
    private readonly IOptions<AniOptions>         _options;
    private readonly ILogger<VibeBiasService>     _log;

    public VibeBiasService(
        IClosedConversationStore     store,
        IOptions<AniOptions>         options,
        ILogger<VibeBiasService>     log)
    {
        _store   = store;
        _options = options;
        _log     = log;
    }

    public async Task<VibeBiasResult> ComputeBiasAsync(
        string                contactName,
        MarkRegisterContext   contextNow,
        CancellationToken     ct = default)
    {
        if (contextNow.MarkRegister.Length != ClosedConversationSummarizer.Registers.Count)
        {
            _log.LogWarning(
                "VibeBias: MarkRegister vector length {Length} != expected {Expected}; returning empty result.",
                contextNow.MarkRegister.Length,
                ClosedConversationSummarizer.Registers.Count);
            return EmptyResult("invalid input vector length");
        }

        var opts = _options.Value;

        // Sufficient lookback to cover the longest half-life * ~2.5 (down to ~17% strength).
        var lookback = TimeSpan.FromDays(opts.VibeBiasLookbackDays);
        var recent = (await _store.GetRecentAsync(
                limit: 100, ct: ct).ConfigureAwait(false))
            .Where(r => contextNow.AsOf - r.ClosedAt <= lookback)
            .ToList();

        if (recent.Count == 0)
            return EmptyResult("no candidate records in lookback window");

        // Build candidate contributions for every eligible record. Filtering
        // by similarity comes AFTER weight computation so AllCandidates
        // (used by telemetry / dashboard diversity histogram) reflects the
        // full pool, not the post-similarity-filter cluster.
        var allCandidates = recent
            .Select(r => BuildContribution(r, contextNow.AsOf, opts))
            .ToList();

        // Filter to records with cosine similarity > threshold against
        // current Mark-register. This is the V1.5.0 Lever 1 "same shape"
        // cluster that competes for surface; out-of-cluster records have
        // bias_weight computed but never enter SurfacedTopN.
        var clusterCandidates = allCandidates
            .Where(c => CosineSimilarity(c.MarkRegister, contextNow.MarkRegister)
                        > opts.VibeBiasSimilarityThreshold)
            .OrderByDescending(c => c.BiasWeight)
            .ToList();

        if (clusterCandidates.Count == 0)
        {
            return new VibeBiasResult(
                AllCandidates:               allCandidates,
                SurfacedTopN:                Array.Empty<VibeBiasContribution>(),
                RecommendedStrategyRegister: ZeroVector(),
                DiversityScoreReason:        "no candidate above similarity threshold");
        }

        var (surfaced, mmrReason) = ApplyMmrRerank(
            clusterCandidates,
            topN:   opts.VibeBiasSurfaceTopN,
            lambda: (float)opts.VibeBiasMmrLambda);

        var recommended = ComputeWeightedRegisterAverage(surfaced);

        return new VibeBiasResult(
            AllCandidates:               allCandidates,
            SurfacedTopN:                surfaced,
            RecommendedStrategyRegister: recommended,
            DiversityScoreReason:        mmrReason);
    }

    private static VibeBiasResult EmptyResult(string reason) =>
        new(AllCandidates:               Array.Empty<VibeBiasContribution>(),
            SurfacedTopN:                Array.Empty<VibeBiasContribution>(),
            RecommendedStrategyRegister: ZeroVector(),
            DiversityScoreReason:        reason);

    private static float[] ZeroVector() =>
        new float[ClosedConversationSummarizer.Registers.Count];

    // ── Helpers (testable via InternalsVisibleTo) ────────────────────────

    /// <summary>
    /// Build a <see cref="VibeBiasContribution"/> for a single record at
    /// the given evaluation time.
    /// </summary>
    internal static VibeBiasContribution BuildContribution(
        ClosedConversationRecord record,
        DateTimeOffset           asOf,
        AniOptions               opts)
    {
        var ageHours       = (float)Math.Max(0, (asOf - record.ClosedAt).TotalHours);
        var saturation     = MaxRegisterPrevalence(record.MarkRegister);
        var importance     = ComputeImportance(
                                record.TurnCount,
                                Math.Abs(record.OutcomeSignalValence),
                                saturation);
        var tier           = DetermineTier(record, opts);
        var halfLifeHours  = TierHalfLife(tier, opts);
        var decay          = ComputeDecay(ageHours, halfLifeHours);
        var biasWeight     = importance * decay;

        return new VibeBiasContribution(
            RecordId:       record.Id,
            BiasWeight:     biasWeight,
            Importance:     importance,
            HalfLifeTier:   tier,
            AgeHours:       ageHours,
            MarkRegister:   ToOrderedVector(record.MarkRegister),
            OutcomeValence: record.OutcomeSignalValence);
    }

    /// <summary>
    /// Importance is the product of three normalized-ish factors:
    /// turn-count (capped/log), absolute outcome valence, and register
    /// saturation depth (max prevalence). Each factor is bounded; the
    /// product gives a single ordering signal that combines "long
    /// conversation," "strong feeling delta," and "concentrated emotional
    /// register" — three orthogonal markers of what makes a conversation
    /// load-bearing rather than throwaway.
    /// </summary>
    internal static float ComputeImportance(
        int    turnCount,
        float  absValence,
        float  registerSaturation)
    {
        // Turn-count factor: 1 turn → ~0.0, 3 turns → ~0.5, 10 turns →
        // ~0.85, 20 turns → ~0.95. Saturating curve keeps "many turns" from
        // dominating "concentrated impact."
        var turnFactor = 1f - MathF.Exp(-turnCount / 5f);

        // Valence and saturation are already in [0, 1].
        var valenceFactor    = Math.Clamp(absValence,        0f, 1f);
        var saturationFactor = Math.Clamp(registerSaturation, 0f, 1f);

        return turnFactor * valenceFactor * saturationFactor;
    }

    /// <summary>
    /// Tier promotion mirroring <c>EmotionalContribution</c>'s
    /// Ambient/Conversation/Global structure. Heavy criteria are
    /// disjunctive (any of: turn count, valence magnitude, or register
    /// saturation crossing its threshold promotes to Heavy). Medium is
    /// the default for substantive interaction; Light catches throwaway
    /// short exchanges with weak emotional signal.
    /// </summary>
    internal static string DetermineTier(
        ClosedConversationRecord record,
        AniOptions               opts)
    {
        var absValence  = Math.Abs(record.OutcomeSignalValence);
        var saturation  = MaxRegisterPrevalence(record.MarkRegister);

        if (record.TurnCount >= opts.VibeBiasHeavyTierMinTurns
            || absValence >= opts.VibeBiasHeavyValenceThreshold
            || saturation >= opts.VibeBiasHeavyRegisterSaturation)
        {
            return "Heavy";
        }

        if (record.TurnCount >= opts.VibeBiasMediumTierMinTurns
            || absValence >= opts.VibeBiasMediumValenceThreshold)
        {
            return "Medium";
        }

        return "Light";
    }

    internal static float TierHalfLife(string tier, AniOptions opts) => tier switch
    {
        "Heavy"  => (float)opts.VibeBiasHeavyTierHalfLifeHours,
        "Medium" => (float)opts.VibeBiasMediumTierHalfLifeHours,
        _        => (float)opts.VibeBiasLightTierHalfLifeHours,
    };

    /// <summary>
    /// Standard exponential decay <c>2^(-t/halfLife)</c>. Same form as
    /// <c>EmotionalContribution.DecayFactor</c>.
    /// </summary>
    internal static float ComputeDecay(float ageHours, float halfLifeHours)
    {
        if (ageHours <= 0f) return 1f;
        if (halfLifeHours <= 0f) return 0f;
        return (float)Math.Pow(2.0, -ageHours / halfLifeHours);
    }

    internal static float ComputeBiasWeight(
        float importance, float ageHours, float halfLifeHours) =>
        importance * ComputeDecay(ageHours, halfLifeHours);

    /// <summary>
    /// MMR-style diversity rerank over the bias candidate cluster.
    /// Identical structural shape to
    /// <c>SqliteMemoryService.ApplyMmrRerank</c> — first pick is highest
    /// bias_weight, subsequent picks maximize <c>weight - λ × maxSim</c>
    /// where similarity is cosine over <c>MarkRegister</c>.
    /// </summary>
    internal static (List<VibeBiasContribution> Surfaced, string Reason) ApplyMmrRerank(
        List<VibeBiasContribution> candidates,
        int                        topN,
        float                      lambda)
    {
        if (candidates.Count == 0 || topN <= 0)
            return (new List<VibeBiasContribution>(), "no candidates");

        var selected  = new List<VibeBiasContribution>(Math.Min(topN, candidates.Count));
        var remaining = new List<VibeBiasContribution>(candidates);

        // First pick: highest bias_weight.
        var first = remaining[0];
        for (var i = 1; i < remaining.Count; i++)
        {
            if (remaining[i].BiasWeight > first.BiasWeight) first = remaining[i];
        }
        selected.Add(first);
        remaining.Remove(first);

        var rejectedNote = string.Empty;

        while (selected.Count < topN && remaining.Count > 0)
        {
            VibeBiasContribution? best = null;
            var bestAdjusted = float.NegativeInfinity;
            VibeBiasContribution? rejectedHigh = null;
            var rejectedHighSim = 0f;

            foreach (var candidate in remaining)
            {
                var maxSimilarity = 0f;
                foreach (var sel in selected)
                {
                    var sim = CosineSimilarity(candidate.MarkRegister, sel.MarkRegister);
                    if (sim > maxSimilarity) maxSimilarity = sim;
                }

                var adjusted = candidate.BiasWeight - lambda * maxSimilarity;
                if (adjusted > bestAdjusted)
                {
                    bestAdjusted = adjusted;
                    best         = candidate;
                }

                // Track the highest-weight rejected candidate for diagnostic
                // narrative — useful for telemetry in V1.5a.
                if (rejectedHigh is null || candidate.BiasWeight > rejectedHigh.BiasWeight)
                {
                    if (candidate != best)
                    {
                        rejectedHigh    = candidate;
                        rejectedHighSim = maxSimilarity;
                    }
                }
            }

            if (best is null) break;
            selected.Add(best);
            remaining.Remove(best);

            if (rejectedHigh is not null && rejectedHigh.RecordId != best.RecordId)
            {
                rejectedNote = $"MMR rejected record {rejectedHigh.RecordId} " +
                               $"(weight={rejectedHigh.BiasWeight:F3}, " +
                               $"cosine={rejectedHighSim:F3} to surfaced)";
            }
        }

        var reason = string.IsNullOrEmpty(rejectedNote)
            ? $"MMR surfaced top {selected.Count} (lambda={lambda:F2})"
            : rejectedNote;
        return (selected, reason);
    }

    /// <summary>
    /// Convert a <c>Dictionary&lt;string, float&gt;</c> register vector
    /// to a fixed-order <c>float[]</c> matching
    /// <see cref="ClosedConversationSummarizer.Registers"/>. Missing keys
    /// resolve to 0; extra keys are ignored.
    /// </summary>
    public static float[] ToOrderedVector(IReadOnlyDictionary<string, float> register)
    {
        var arr = new float[ClosedConversationSummarizer.Registers.Count];
        for (var i = 0; i < ClosedConversationSummarizer.Registers.Count; i++)
        {
            register.TryGetValue(ClosedConversationSummarizer.Registers[i], out var v);
            arr[i] = v;
        }
        return arr;
    }

    /// <summary>
    /// Maximum prevalence value across the register vector — V1.5.0 uses
    /// this as the "register saturation depth" component of importance
    /// and as the Heavy-tier promotion criterion.
    /// </summary>
    internal static float MaxRegisterPrevalence(IReadOnlyDictionary<string, float> register)
    {
        if (register.Count == 0) return 0f;
        var max = 0f;
        foreach (var v in register.Values)
        {
            if (v > max) max = v;
        }
        return max;
    }

    /// <summary>
    /// Weighted average of the surfaced contributions' MarkRegister
    /// vectors, weighted by bias_weight. Renders the
    /// <c>RecommendedStrategyRegister</c> for V1.5a divergence telemetry
    /// (mood vs vibe vs actual response register triple).
    /// </summary>
    internal static float[] ComputeWeightedRegisterAverage(
        IReadOnlyList<VibeBiasContribution> surfaced)
    {
        var len = ClosedConversationSummarizer.Registers.Count;
        if (surfaced.Count == 0) return new float[len];

        var totalWeight = 0f;
        foreach (var c in surfaced) totalWeight += c.BiasWeight;
        if (totalWeight <= 0f) return new float[len];

        var avg = new float[len];
        foreach (var c in surfaced)
        {
            for (var i = 0; i < len; i++)
            {
                avg[i] += c.MarkRegister[i] * (c.BiasWeight / totalWeight);
            }
        }
        return avg;
    }

    private static float CosineSimilarity(float[] a, float[] b) =>
        VectorMath.CosineSimilarity(a, b, zeroDenomValue: 0f);
}
