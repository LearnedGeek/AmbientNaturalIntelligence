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

        // §4.4 contact-state slice (Theme M Phase M.4, May 28, 2026).
        // Reads ContactStatePerceptionSource outputs from snapshot.Perceptions
        // (these are NOT persisted by PerceptionPhase per Apr 2026 design
        // — they're "Mark is probably X" routine guesses, fine as conscious
        // context but poison for retrieval). Slice surfaces current routine
        // awareness without polluting the verifier substrate.
        //
        // M.4 ships the slice only; the outreach-consumer extension named
        // in the original §4.4 plan is deferred (out of fast-track scope —
        // requires separate observation gate per Mark + Claude joint review).
        var contactStateSlice = ComposeContactStateSlice(snapshot.Perceptions);

        // §4.2 inner-thought-aggregate slice (Theme M Phase M.5-lite,
        // May 28, 2026). The full §4.2 design calls for LLM paraphrase
        // generation across recent inner thoughts with cosine-distance
        // anti-verbatim checks — that's M.5-full, deferred. M.5-lite
        // ships the slice using PRE-AGGREGATED snapshot fields
        // (DominantRegister + SimilarRecentThoughts count) — zero parrot
        // risk because the source data is already synthesized aggregate
        // signal, not raw inner-thought text. Reduced richness vs the
        // §4.2 spec but immediate substrate-thinness contribution and
        // safe by construction.
        var innerThoughtSlice = ComposeInnerThoughtAggregateSlice(
            snapshot.DominantRegister,
            snapshot.SimilarRecentThoughts);

        // §4.4 contact-state slice (Theme M Phase M.4, May 28, 2026).
        // Reads ContextSnapshot.RecentClosedConversation (Vibe Loop V1.4
        // substrate). Anti-parrot already enforced at gist-generation time
        // (V1.2 constraint). Theme J.5h Validity filter excludes the 26
        // quarantined fabrication records. §4.1 fresh-thread exclusion
        // (<30 min) avoids duplicating active-thread context the model
        // already has.
        var closedConversationSlice = ComposeClosedConversationSlice(
            snapshot.RecentClosedConversation,
            DateTimeOffset.UtcNow);

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

        // Compose: §4.6 slice ordering — tension → register →
        // closed-conversation → inner-thought-aggregate → contact-state
        // → world-self. All six slices now ship at M.5-lite.
        var sliceParts = new List<string>(6);
        var flags = new GistSliceFlags
        {
            TensionState           = !string.IsNullOrEmpty(tensionSlice),
            RegisterState          = !string.IsNullOrEmpty(registerSlice),
            ClosedConversation     = !string.IsNullOrEmpty(closedConversationSlice),
            InnerThoughtAggregate  = !string.IsNullOrEmpty(innerThoughtSlice),
            ContactState           = !string.IsNullOrEmpty(contactStateSlice),
            WorldSelf              = !string.IsNullOrEmpty(worldSelfSlice),
        };
        if (flags.TensionState)          sliceParts.Add(tensionSlice);
        if (flags.RegisterState)         sliceParts.Add(registerSlice);
        if (flags.ClosedConversation)    sliceParts.Add(closedConversationSlice);
        if (flags.InnerThoughtAggregate) sliceParts.Add(innerThoughtSlice);
        if (flags.ContactState)          sliceParts.Add(contactStateSlice);
        if (flags.WorldSelf)             sliceParts.Add(worldSelfSlice);

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
            TensionState          = ApproxTokens(tensionSlice),
            RegisterState         = ApproxTokens(registerSlice),
            ClosedConversation    = ApproxTokens(closedConversationSlice),
            InnerThoughtAggregate = ApproxTokens(innerThoughtSlice),
            ContactState          = ApproxTokens(contactStateSlice),
            WorldSelf             = ApproxTokens(worldSelfSlice),
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
    /// Compose the §4.1 closed-conversation slice (Theme M Phase M.3,
    /// May 28, 2026). Returns the slice text or empty string when no
    /// usable closed-conversation substrate is available.
    ///
    /// <para>
    /// **Producer:** <see cref="ContextSnapshot.RecentClosedConversation"/>
    /// — Vibe Loop V1.4 substrate. The gist text is already anti-parrot-
    /// constrained at generation time (V1.2 LMKit summarizer).
    /// </para>
    ///
    /// <para>
    /// **Substrate hygiene (Theme J.5h):** records with
    /// <c>Validity != "valid"</c> are quarantined fabrications (the 28
    /// records identified in the 2026-05-21 audit). The slice silently
    /// excludes them — never injected into runtime prompts.
    /// </para>
    ///
    /// <para>
    /// **Fresh-thread exclusion (§4.1):** threads closed less than 30
    /// minutes ago are excluded to avoid duplicating active-thread
    /// context the conversation reply model already has via
    /// <c>RecentHistory</c>.
    /// </para>
    ///
    /// <para>
    /// **Slice content:** *what just happened between us* in canonical
    /// paraphrased form — gist text + age annotation + dominant register
    /// at close. Outcome-valence annotation is omitted intentionally
    /// because the tension-state slice (§4.8) already surfaces it
    /// ("last conversation left me regulated well (+0.45)"); §4.6
    /// composition rule allows slice reduction when an upstream slice
    /// covers the same ground.
    /// </para>
    /// </summary>
    internal static string ComposeClosedConversationSlice(
        ClosedConversationRecord? closed,
        DateTimeOffset now)
    {
        if (closed is null) return string.Empty;
        if (!string.Equals(closed.Validity, "valid", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (string.IsNullOrWhiteSpace(closed.Gist)) return string.Empty;

        var age = now - closed.ClosedAt;
        if (age < TimeSpan.FromMinutes(30)) return string.Empty;

        var ageLabel = age.TotalHours < 24
            ? $"{Math.Max(1, (int)age.TotalHours)}h ago"
            : $"{Math.Max(1, (int)age.TotalDays)}d ago";

        var registerClause = ResolveDominantRegister(closed.AniRegister) is { } register
            ? $"; dominant register: {register}"
            : string.Empty;

        return $"recent-thread: closed {ageLabel}, {closed.Gist.Trim()}{registerClause}.";
    }

    private static string? ResolveDominantRegister(IReadOnlyDictionary<string, float>? register)
    {
        if (register is null || register.Count == 0) return null;
        var top = register
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(top.Key) ? null : top.Key.ToLowerInvariant();
    }

    /// <summary>
    /// Compose the §4.2 inner-thought-aggregate slice — Theme M Phase
    /// M.5-lite (May 28, 2026). Returns the slice text or empty string
    /// when no aggregate signal is available. Internal for testing.
    ///
    /// <para>
    /// **M.5-lite vs full §4.2 design:** the §4.2 spec calls for LLM
    /// paraphrase generation across recent inner-thought records with
    /// cosine-distance anti-verbatim checks against each source. That
    /// generation step + its parrot-safety machinery is M.5-full, deferred.
    /// M.5-lite ships the slice using PRE-AGGREGATED snapshot fields
    /// (<see cref="ContextSnapshot.DominantRegister"/> +
    /// <see cref="ContextSnapshot.SimilarRecentThoughts"/> count) — zero
    /// parrot risk because the source data is ALREADY synthesized
    /// aggregate signal, not raw inner-thought text.
    /// </para>
    ///
    /// <para>
    /// **§4.2 generation invariant satisfied by construction:** because
    /// M.5-lite does not read raw inner-thought content, it cannot
    /// possibly lift verbatim phrasing from any single record. The
    /// cosine-distance check the full M.5 spec calls for is unnecessary
    /// at this scope.
    /// </para>
    ///
    /// <para>
    /// **Reduced richness vs the §4.2 design:** the slice surfaces
    /// "she's been holding tenderness-register thinking across N recent
    /// reflections" rather than "she's been thinking about how the
    /// bookshelf creaks in the rain." Less evocative; safer; immediate
    /// substrate-thinness contribution. M.5-full can supersede when the
    /// LLM-generation-with-cosine-check infrastructure is wired.
    /// </para>
    /// </summary>
    internal static string ComposeInnerThoughtAggregateSlice(
        string?                      dominantRegister,
        IReadOnlyList<MemoryRecord>? similarRecentThoughts)
    {
        var parts = new List<string>();

        var thoughtCount = similarRecentThoughts?.Count ?? 0;
        if (thoughtCount > 0)
        {
            parts.Add(thoughtCount == 1
                ? "1 recent thread of reflection"
                : $"{thoughtCount} recent threads of reflection");
        }

        if (!string.IsNullOrWhiteSpace(dominantRegister))
        {
            parts.Add($"holding {dominantRegister.Trim().ToLowerInvariant()}-register");
        }

        if (parts.Count == 0) return string.Empty;
        return "inner-thought-aggregate: " + string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// Compose the §4.4 contact-state slice (Theme M Phase M.4, May 28, 2026).
    /// Returns the slice text or empty string when no contact-state perceptions
    /// are available. Internal for testing.
    ///
    /// <para>
    /// **Source:** <c>ContactStatePerceptionSource</c>-emitted entries in
    /// <see cref="ContextSnapshot.Perceptions"/> (SourceName == "contact-state").
    /// These perceptions are intentionally NOT persisted by
    /// <c>PerceptionPhase</c> (Apr 2026 design: routine guesses like "Mark
    /// is probably at the gym" match too many queries and poison retrieval).
    /// Reading them for the conscious-substrate gist is exactly the
    /// architectural intent — they're context for Ani's current frame, not
    /// claims for verifier substrate.
    /// </para>
    ///
    /// <para>
    /// **Slice content (§4.4):** *what Mark has been doing in life right now*
    /// — current routine awareness. Takes the two most-recent contact-state
    /// perceptions by <see cref="PerceptionEvent.OccurredAt"/> and renders
    /// their summaries.
    /// </para>
    ///
    /// <para>
    /// **M.4 scope:** the slice only. The original §4.4 plan also extends
    /// the consumer surface from <c>ConversationReplyPhase</c> to
    /// <c>OutreachPhase</c> behind a separate feature flag; that extension
    /// is deferred per the fast-track scope decision (2026-05-28).
    /// </para>
    /// </summary>
    internal static string ComposeContactStateSlice(
        IReadOnlyList<PerceptionEvent>? perceptions)
    {
        if (perceptions is null || perceptions.Count == 0) return string.Empty;

        var contactState = perceptions
            .Where(p => string.Equals(p.SourceName, "contact-state",
                                      StringComparison.OrdinalIgnoreCase))
            .Where(p => !string.IsNullOrWhiteSpace(p.Summary))
            .OrderByDescending(p => p.OccurredAt)
            .Take(2)
            .Select(p => p.Summary.Trim())
            .ToList();

        if (contactState.Count == 0) return string.Empty;
        return "contact-state: Mark right now — " + string.Join("; ", contactState) + ".";
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
