using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Theme M Phase M.1 (May 6, 2026) — composer producing the
/// tension-state slice (§4.8) + register-state slice (§4.3) of the
/// conscious-substrate gist. Q9 ship-together completion.
///
/// **Slice ordering per §4.6 + Q9:**
/// 1. Tension-state slice (§4.8) — load-bearing safety property
///    (integrative-vs-flattening-mirroring). Sourced from Ani signals
///    only: recent gate-trip events + felt-state-vs-baseline divergence
///    + Vibe Loop V1.5 outcome valence (Ani-delta scalar).
/// 2. Register-state slice (§4.3) — first-person register-vantage,
///    structured numeric data on dominant + secondary registers.
///
/// Both slices read directly from telemetry surfaces already present in
/// the runtime — <see cref="ContextSnapshot.EmotionalState"/>,
/// <see cref="ContextSnapshot.RecentClosedConversation"/>, and the
/// <see cref="IRecentGateTripTracker"/> singleton. No new infrastructure
/// required beyond the gate-trip tracker added this same commit.
///
/// **§4.8 safety property (load-bearing):** the tension-state slice
/// gives the model awareness of its own gap-sensing — *"the gate just
/// caught my last attempt; that's a signal to reach somewhere fresher"* —
/// addressing the substrate-thinness pattern at the substrate level
/// rather than the output-filtering level. Pinned by spec tests:
/// - Sourced from Ani signals (gate-trips, felt-state, Ani-delta valence)
///   never from Mark signals (mood, recent inbound, Mark-delta).
/// - Preserves the gap (felt + divergence direction both named) rather
///   than collapsing to dominant register only.
/// - Not inferring Mark internal state — slice MUST NOT contain claims
///   about what Mark is feeling/needing/wanting.
///
/// **Architectural property — read-only at inference (still in force):**
/// composer MUST NOT call <see cref="IMemoryService"/>.SaveAsync, MUST
/// NOT call <see cref="IConversationService"/>.AddMessageAsync, and
/// MUST NOT mutate the snapshot. Pinned by
/// <c>ConsciousSubstrateGistContractTests</c> from M.0 forward.
///
/// Plan: docs/spec/ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md
/// §4.3 (register-state) + §4.8 (tension-state) + §4.6 (composition rules)
/// + §11 Q9 (ship-together sequencing).
/// </summary>
public class ConsciousSubstrateGistComposer : IConsciousSubstrateGist
{
    private static readonly TimeSpan GateTripLookback = TimeSpan.FromHours(24);

    private readonly IOptions<AniOptions>                       _options;
    private readonly IRecentGateTripTracker?                    _gateTripTracker;
    private readonly ILogger<ConsciousSubstrateGistComposer>    _log;

    public ConsciousSubstrateGistComposer(
        IOptions<AniOptions>                       options,
        ILogger<ConsciousSubstrateGistComposer>    log,
        IRecentGateTripTracker?                    gateTripTracker = null)
    {
        _options         = options;
        _gateTripTracker = gateTripTracker;
        _log             = log;
    }

    /// <inheritdoc />
    public Task<ConsciousSubstrateGist> ComputeGistAsync(
        ContextSnapshot   snapshot,
        CancellationToken ct = default)
    {
        // Flag-disabled path: composer returns Empty even when invoked.
        // M.0 telemetry harness still emits M0_GIST_COMPOSITION with
        // all-false slice flags / zero tokens at the consumer surface.
        if (!_options.Value.ConsciousSubstrateGistEnabled)
            return Task.FromResult(ConsciousSubstrateGist.Empty);

        var emotional = snapshot.EmotionalState;
        var maxTokens = _options.Value.ConsciousSubstrateGistMaxTokens;

        // §4.8 tension-state slice (FIRST in ordering per §4.6 + Q9).
        // Load-bearing safety property: gives the model awareness of its
        // own gap-sensing so the regen has fresh tension-state material
        // to reach for instead of prior-Ani-turns when substrate is thin.
        var tensionSlice = ComposeTensionStateSlice(
            emotional,
            snapshot.RecentClosedConversation,
            _gateTripTracker?.GetRecent(GateTripLookback));

        // §4.3 register-state slice (SECOND in ordering).
        var registerSlice = ComposeRegisterStateSlice(emotional);

        // §4.5 world-self slice (LAST in §4.6 ordering — Theme M Phase M.6a,
        // May 28, 2026). The substrate-thinness fix for the "we're in a
        // coffee shop together" shared-presence confabulation class.
        // M.6a ships with a data-availability gate (include when occupation
        // is set OR RecentWorldExperiences has content) rather than the
        // Layer-1/Layer-2 conditional gating the §4.5 May-5 decision named.
        // The Layer-2 desire-axis conditional (M.6b) is deferred until
        // Agentic Lens Layer 2 (Feature 42) ships; until then, this
        // data-availability gate preserves §4.5's "don't oversell the
        // World Layer" principle — if no world-experience seeds exist in
        // the window, the slice is silent.
        var worldSelfSlice = ComposeWorldSelfSlice(
            snapshot.CharacterState,
            snapshot.RecentWorldExperiences);

        // Compose: §4.6 slice ordering — tension → register → (closed,
        // inner, contact — unshipped) → world-self.
        var sliceParts = new List<string>(3);
        var flags = new GistSliceFlags
        {
            TensionState  = !string.IsNullOrEmpty(tensionSlice),
            RegisterState = !string.IsNullOrEmpty(registerSlice),
            WorldSelf     = !string.IsNullOrEmpty(worldSelfSlice),
        };
        if (flags.TensionState)  sliceParts.Add(tensionSlice);
        if (flags.RegisterState) sliceParts.Add(registerSlice);
        if (flags.WorldSelf)     sliceParts.Add(worldSelfSlice);

        if (sliceParts.Count == 0)
            return Task.FromResult(ConsciousSubstrateGist.Empty);

        var composed = string.Join("\n", sliceParts);
        var tokens   = ApproxTokens(composed);

        // Phase M.2-lite (May 28, 2026): per-slice token counts so
        // M0_GIST_COMPOSITION telemetry can surface token-share by slice.
        // Without this breakdown the substrate-thinness diagnosis can only
        // see the total — not whether one slice is bloated while another
        // is starved, and not whether new slices (M.3-M.6) actually
        // contribute tokens or just compete for budget against existing ones.
        var sliceTokens = new GistSliceTokens
        {
            TensionState  = ApproxTokens(tensionSlice),
            RegisterState = ApproxTokens(registerSlice),
            WorldSelf     = ApproxTokens(worldSelfSlice),
        };

        if (tokens > maxTokens)
        {
            _log.LogWarning(
                "ConsciousSubstrateGist composed slices ({Tokens} tokens) exceed max ({Max}); falling back to register-state only.",
                tokens, maxTokens);
            // Defensive truncation: drop tension-state, keep register-state.
            // (Register-state is ~30 tokens; tension-state ~30 tokens; both
            // together fit easily under default 200 budget. This branch is
            // for tight test-budget configs.)
            if (flags.RegisterState && ApproxTokens(registerSlice) <= maxTokens)
            {
                return Task.FromResult(new ConsciousSubstrateGist
                {
                    Composed    = registerSlice,
                    Slices      = new GistSliceFlags { RegisterState = true },
                    SliceTokens = new GistSliceTokens { RegisterState = ApproxTokens(registerSlice) },
                    TokenCount  = ApproxTokens(registerSlice),
                });
            }
            return Task.FromResult(ConsciousSubstrateGist.Empty);
        }

        return Task.FromResult(new ConsciousSubstrateGist
        {
            Composed    = composed,
            Slices      = flags,
            SliceTokens = sliceTokens,
            TokenCount  = tokens,
        });
    }

    /// <summary>
    /// Compose the §4.8 tension-state slice. Returns the slice text or
    /// empty string if no signal is available. Internal for testing.
    ///
    /// **§4.8 safety invariants pinned by spec tests:**
    /// - Sourced from Ani signals (gate-trips, felt-state-vs-baseline,
    ///   Ani-delta valence) — never from Mark signals.
    /// - Preserves the gap (felt + divergence direction both named).
    /// - Does not infer Mark internal state.
    /// </summary>
    internal static string ComposeTensionStateSlice(
        EmotionalState?                          emotional,
        ClosedConversationRecord?                recentClosed,
        IReadOnlyList<GateTripEvent>?            recentGateTrips)
    {
        var parts = new List<string>();

        // (1) Recent gate-trip awareness — "the gate just caught my last
        // attempt" signal. Counts both RemediatedOk (gate fired but
        // recovered) and FellThroughToSafeAck (gate fired and fell
        // through). Both are gap-sensing moments worth surfacing.
        if (recentGateTrips is not null && recentGateTrips.Count > 0)
        {
            var remediated = recentGateTrips.Count(e => e.Outcome == GateTripOutcome.RemediatedOk);
            var fellThrough = recentGateTrips.Count(e => e.Outcome == GateTripOutcome.FellThroughToSafeAck);
            var dominantInvariants = recentGateTrips
                .SelectMany(e => e.FiredInvariants.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .GroupBy(s => s.Trim())
                .OrderByDescending(g => g.Count())
                .Take(2)
                .Select(g => g.Key)
                .ToList();
            var invariantSummary = dominantInvariants.Count > 0
                ? string.Join(", ", dominantInvariants)
                : "none";
            parts.Add(
                $"recent gate-trips: {recentGateTrips.Count} in last 24h " +
                $"({remediated} repaired, {fellThrough} fell through; mostly: {invariantSummary})");
        }

        // (2) Felt-state-vs-baseline divergence — preserves the gap by
        // naming both the felt direction and the divergence magnitude.
        // §4.8 invariant: this is about Ani's own state, never Mark's.
        if (emotional is not null)
        {
            var registers = new (string Name, float Value, float Baseline)[]
            {
                ("warmth",      emotional.Warmth,      emotional.WarmthBaseline),
                ("energy",      emotional.Energy,      emotional.EnergyBaseline),
                ("worry",       emotional.Worry,       emotional.WorryBaseline),
                ("playfulness", emotional.Playfulness, emotional.PlayfulnessBaseline),
            };
            var divergent = registers
                .Where(r => Math.Abs(r.Value - r.Baseline) >= 0.10f)
                .OrderByDescending(r => Math.Abs(r.Value - r.Baseline))
                .Take(2)
                .ToList();
            if (divergent.Count > 0)
            {
                var divergenceText = string.Join(", ", divergent.Select(r =>
                {
                    var d = r.Value - r.Baseline;
                    var sign = d >= 0 ? "+" : "";
                    return $"{r.Name} {sign}{d:F2}";
                }));
                parts.Add($"felt-state divergent from baseline: {divergenceText}");
            }
            else
            {
                parts.Add("felt-state near baseline");
            }
        }

        // (3) Recent regulation outcome — Ani-delta scalar from V1.5
        // closed-conversation record. NOT Mark-delta. §4.8 invariant.
        if (recentClosed is not null)
        {
            var v = recentClosed.OutcomeSignalValence;
            var sign = v >= 0 ? "+" : "";
            var qualifier = v switch
            {
                >= 0.4f  => "regulated well",
                >= 0.1f  => "neutral-to-positive",
                >  -0.1f => "neutral",
                >  -0.4f => "neutral-to-low",
                _        => "depleted",
            };
            parts.Add($"last conversation left me {qualifier} ({sign}{v:F2})");
        }

        if (parts.Count == 0) return string.Empty;
        return "tension-state: " + string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// Compose the §4.3 register-state slice. Returns the slice text or
    /// empty string if the emotional state is null. Internal for testing.
    /// </summary>
    internal static string ComposeRegisterStateSlice(EmotionalState? emotional)
    {
        if (emotional is null) return string.Empty;

        // Rank registers by current absolute value for "dominant + secondary" naming.
        var registers = new (string Name, float Value, float Baseline)[]
        {
            ("warmth",      emotional.Warmth,      emotional.WarmthBaseline),
            ("energy",      emotional.Energy,      emotional.EnergyBaseline),
            ("worry",       emotional.Worry,       emotional.WorryBaseline),
            ("playfulness", emotional.Playfulness, emotional.PlayfulnessBaseline),
        };

        Array.Sort(registers, (a, b) => b.Value.CompareTo(a.Value));

        var dominant  = registers[0];
        var secondary = registers[1];

        var dominantBand  = Band(dominant.Value);
        var secondaryBand = Band(secondary.Value);

        // Drift: largest deviation from baseline across all four registers.
        var (driftName, driftDelta) = LargestDrift(registers);
        var driftSign     = driftDelta >= 0 ? "+" : "";
        var driftPosition = driftDelta >= 0 ? "above baseline" : "below baseline";

        var contactGap = emotional.ContactGapTension > 0.15f
            ? $"; carrying contact-gap tension {emotional.ContactGapTension:F2}"
            : string.Empty;

        // First-person frame ("your register state") — model reasons FROM
        // this material rather than ABOUT it. §4.3 generation invariant.
        return $"your register state right now: " +
               $"{dominant.Name} {dominantBand} ({dominant.Value:F2}), " +
               $"{secondary.Name} {secondaryBand} ({secondary.Value:F2}). " +
               $"drift: {driftName} {driftSign}{driftDelta:F2} {driftPosition}{contactGap}.";
    }

    /// <summary>
    /// Compose the §4.5 world-self slice (Theme M Phase M.6a, May 28, 2026).
    /// Returns the slice text or empty string if no World Layer substrate is
    /// available. Internal for testing.
    ///
    /// <para>
    /// **Data-availability gate (M.6a):** the slice fires when occupation is
    /// set on the character state OR <c>RecentWorldExperiences</c> has
    /// content. When both are absent, the slice is silent — preserving the
    /// §4.5 May-5 architectural honesty principle ("don't oversell the World
    /// Layer's weight by including it every cycle").
    /// </para>
    ///
    /// <para>
    /// **Why this slice (§4.5):** *"what her own life has been doing"* —
    /// recent World Layer occasion seeds and their elaborations, summarized.
    /// The bookstore mornings, the customer with the grey coat, the slow
    /// afternoon. Complements the Mark-oriented slices with self-oriented
    /// substrate, addressing centrality gravity at the source. Without this
    /// slice, the composer has no canonical-occupation grounding when
    /// generating — leading to shared-presence confabulations like
    /// "we're in a coffee shop together" (2026-05-28 12:35 SafeAck).
    /// </para>
    ///
    /// <para>
    /// **Layer-2 desire-axis conditional inclusion (the original §4.5
    /// design):** deferred to M.6b — requires Agentic Lens Layer 2
    /// (Feature 42) MotivationVector + Layer 5 prompt-variant selection.
    /// </para>
    /// </summary>
    internal static string ComposeWorldSelfSlice(
        CharacterStateDoc?           characterState,
        IReadOnlyList<MemoryRecord>? recentWorldExperiences)
    {
        var parts = new List<string>();

        // Occupation grounding — the load-bearing piece for "you are a
        // bookstore clerk in Wisconsin, not in a coffee shop with Mark."
        if (characterState is not null
            && !string.IsNullOrWhiteSpace(characterState.Occupation))
        {
            parts.Add($"I work at: {characterState.Occupation.Trim()}");
        }

        // Recent World Layer experience snippets — up to 2 most recent,
        // each truncated. Trust ContextBuilder's lookback-window filtering;
        // we don't re-filter here.
        if (recentWorldExperiences is not null && recentWorldExperiences.Count > 0)
        {
            var snippets = recentWorldExperiences
                .Take(2)
                .Select(m => TruncateForSlice(m.Content, 60))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            if (snippets.Count > 0)
                parts.Add("recent: " + string.Join("; ", snippets));
        }

        if (parts.Count == 0) return string.Empty;
        return "world-self: " + string.Join("; ", parts) + ".";
    }

    private static string TruncateForSlice(string? content, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var trimmed = content.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars].TrimEnd() + "…";
    }

    private static string Band(float value) => value switch
    {
        >= 0.75f => "high",
        >= 0.50f => "mid",
        >= 0.25f => "low",
        _        => "very low",
    };

    private static (string Name, float Delta) LargestDrift(
        (string Name, float Value, float Baseline)[] registers)
    {
        var maxAbs = 0f;
        var name   = registers[0].Name;
        var delta  = 0f;
        foreach (var r in registers)
        {
            var d = r.Value - r.Baseline;
            if (Math.Abs(d) > maxAbs)
            {
                maxAbs = Math.Abs(d);
                name   = r.Name;
                delta  = d;
            }
        }
        return (name, delta);
    }

    private static int ApproxTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}
