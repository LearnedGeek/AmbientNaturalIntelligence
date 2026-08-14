namespace AniRuntime.Core.Models;

/// <summary>
/// A single emotional impact from a thought, conversation, or event.
/// Each contribution starts at its scored magnitude and decays exponentially
/// via a half-life. The emotional state at any moment is the sum of all
/// active contributions on top of personality baselines.
///
/// This replaces the old model where deltas were applied permanently and
/// a global drift pulled everything back. Now each thought has its own
/// lifecycle — strong initial impact that fades naturally.
/// </summary>
public class EmotionalContribution
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Summary of the source content (for semantic dedup and theme tracking).</summary>
    public string SourceContent { get; set; } = string.Empty;

    /// <summary>Initial scored deltas — the contribution at full strength.</summary>
    public float WarmthDelta { get; set; }
    public float EnergyDelta { get; set; }
    public float WorryDelta { get; set; }
    public float PlayfulnessDelta { get; set; }

    /// <summary>When this contribution was created (or last refreshed).</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Exponential decay half-life in hours. After one half-life, the contribution
    /// is at 50% strength. After two, 25%. After ~7 half-lives it's effectively zero.
    /// </summary>
    public float HalfLifeHours { get; set; } = 1.0f;

    /// <summary>
    /// Impact category determines max delta and half-life.
    /// Ambient = inner thoughts (low impact, fast decay).
    /// Conversation = direct interaction (higher impact, slower decay).
    /// Global = major events (moderate impact, slowest decay).
    /// </summary>
    public ImpactCategory Category { get; set; } = ImpactCategory.Ambient;

    /// <summary>
    /// Intensity within the emotional register (0.0–1.0). Applied as a multiplier
    /// to the raw deltas before tier ceiling clamping. Default 1.0 for backward compat.
    /// Phase 2 uses this for tier promotion: severity ≥ 0.85 → Global tier.
    /// </summary>
    public float Severity { get; set; } = 1.0f;

    /// <summary>
    /// Emotional register determined by LLM scoring (e.g., "Playfulness", "Tenderness",
    /// "Curiosity"). Used by RegisterTracker for dashboard heatmap and auto-model gating.
    /// Maps to one of the 9 register families in the Emotion Taxonomy v1.3.
    /// </summary>
    public string Register { get; set; } = "Wistful";

    /// <summary>
    /// C3 Associative Spark flag — signals that this thought has natural outreach
    /// potential ("something made me think of you") independent of the desire threshold.
    /// </summary>
    public bool IsOutreachReady { get; set; }

    /// <summary>Optional embedding for semantic similarity checks.</summary>
    public float[]? Embedding { get; set; }

    // ── ML classification (LM-Kit) — dual-signal: state + expression ─────────
    // Stored alongside heuristic Register for divergence tracking.
    // Null until LM-Kit classifies (lazy, async).

    /// <summary>LM-Kit detected emotion from text content (e.g., "sadness", "love", "curiosity").</summary>
    public string? MLEmotion { get; set; }

    /// <summary>LM-Kit emotion classification confidence (0.0–1.0).</summary>
    public float? MLConfidence { get; set; }

    /// <summary>Whether LM-Kit detected sarcasm in the text.</summary>
    public bool? MLSarcasmDetected { get; set; }

    /// <summary>
    /// Divergence score: how much the ML expression differs from the heuristic state.
    /// 0.0 = aligned (state and expression agree), 1.0 = fully divergent.
    /// Null until both classifications are available.
    /// </summary>
    public float? DivergenceScore { get; set; }

    /// <summary>
    /// Associative anchor: the vivid detail extracted from this thought that seeds
    /// the next cycle's creative drift. Stored for dashboard visualization of
    /// associative drift chains.
    /// </summary>
    public string? AssociativeAnchor { get; set; }

    /// <summary>
    /// EmoLLaMA-7B substrate vector, serialised JSON. Contains the 15-axis
    /// substrate emitted by <c>IEmotionalSubstrateScorer</c> (ei.*, ec.*,
    /// dim.valence) plus the schema id. Populated by the persist-mode
    /// substrate scorer at contribution time and by <c>--backfill-substrate</c>
    /// retroactively. Retires the Issue #68 "shadow-mode / follow-on PR" TODO —
    /// the vector was computed but discarded from May 28, 2026 through the
    /// 2026-08-12 flip.
    /// </summary>
    public string? SubstrateJson { get; set; }

    /// <summary>
    /// Compute the decay factor at a given point in time.
    /// Returns a multiplier 0.0–1.0 to apply to the initial deltas.
    /// </summary>
    public float DecayFactor(DateTimeOffset asOf)
    {
        var elapsed = (float)(asOf - CreatedAt).TotalHours;
        if (elapsed <= 0f) return 1.0f;
        if (HalfLifeHours <= 0f) return 0f;

        // 2^(-t/halfLife) — standard exponential decay
        return (float)Math.Pow(2.0, -elapsed / HalfLifeHours);
    }

    /// <summary>Current effective deltas after severity scaling and decay.</summary>
    public (float Warmth, float Energy, float Worry, float Playfulness) CurrentDeltas(DateTimeOffset asOf)
    {
        var factor = DecayFactor(asOf) * Severity;
        return (
            WarmthDelta * factor,
            EnergyDelta * factor,
            WorryDelta * factor,
            PlayfulnessDelta * factor
        );
    }

    /// <summary>
    /// Whether this contribution has decayed below a meaningful threshold.
    /// Used to determine when to move it to "processed" status.
    /// </summary>
    public bool IsEffectivelyZero(DateTimeOffset asOf, float epsilon = 0.02f)
    {
        // May 2, 2026 calibration: epsilon tightened from 0.005 → 0.02. Pre-May-2
        // contributions stayed "active" until they decayed below 0.005, which for
        // an Ambient max-delta-0.15 contribution meant ~5 hours of pool time. That
        // produced ~50 active Ambient contributions in steady-state, contributing
        // to the May 1 evening saturation observation. New epsilon drops Ambient
        // contributions at ~3 half-lives (~1.5h with the May 2 0.5h half-life)
        // when they're already below 13% of original strength — they're no longer
        // moving the dial meaningfully and shouldn't count toward the active pool.
        var (w, e, c, p) = CurrentDeltas(asOf);
        return Math.Abs(w) < epsilon
            && Math.Abs(e) < epsilon
            && Math.Abs(c) < epsilon
            && Math.Abs(p) < epsilon;
    }
}

public enum ImpactCategory
{
    /// <summary>Inner thoughts, routine observations. Max delta 0.15, half-life 0.5h (May 2 calibration).</summary>
    Ambient,

    /// <summary>Direct conversation with contact. Max delta 0.25, half-life 1.5h (May 2 calibration).</summary>
    Conversation,

    /// <summary>Major news, life events. Max delta 0.35, half-life 6h (May 2 calibration).</summary>
    Global
}

/// <summary>
/// Constants for impact category parameters, kept centralized so cognitive cycle
/// and prompt builder use the same values.
///
/// **May 2, 2026 calibration** — half-lives halved across all three tiers
/// after May 1 evening observed state pegged at warmth=0.99 / worry=0.93 /
/// playfulness=0.95 simultaneously. The pre-May-2 half-lives (1h / 3h /
/// 12h) combined with the model's uniform-positive delta bias on three
/// dimensions (W+/C+/P+ on 75–85% of contributions) produced steady-state
/// `deltaSum ≈ rate × halfLife / ln(2)` of ~2.16 for ambient cycles
/// alone — high enough that <c>tanh(deltaSum / 1.5)</c> sat in the flat
/// region (~0.89), pinning state near saturation and making new events
/// unable to move the needle.
///
/// Tighter half-lives reduce the steady-state deltaSum proportionally,
/// keeping tanh in the responsive slope region. Calibration: state still
/// elevates noticeably after sustained positive events, but doesn't peg
/// — and new events register as visible state changes.
///
/// If empirical observation shows the new values are too aggressive
/// (state too volatile, doesn't sustain genuine emotional arcs), revert
/// or tune intermediately. The previous values are documented above for
/// rollback context.
/// </summary>
public static class ImpactCategoryDefaults
{
    public static (float MaxDelta, float HalfLifeHours) GetDefaults(ImpactCategory category) => category switch
    {
        ImpactCategory.Ambient      => (0.15f, 0.5f),
        ImpactCategory.Conversation => (0.25f, 1.5f),
        ImpactCategory.Global       => (0.35f, 6.0f),
        _                           => (0.15f, 0.5f),
    };

    /// <summary>
    /// Maps an LLM-scored register string to one of the 11 register families
    /// defined in the Emotion Taxonomy v1.5. Used by RegisterTracker for
    /// dashboard heatmap and auto-model generation gating.
    ///
    /// <para>Taxonomy history:</para>
    /// <list type="bullet">
    /// <item>v1.4 — Resilience added after emerging from deployment data (Mar 20, 2026).</item>
    /// <item>v1.5 — Yearning added 2026-08-14 after Mark identified an unnameable
    /// register class ANI had drifted into ("angst + poetic warmth + dreamy" —
    /// forward-facing reaching for imagined intimacy with braced vulnerability).
    /// Distinct from Longing (missing-present-absent-person) and Wistful
    /// (bittersweet-about-past). Yearning is forward-facing with vulnerability.
    /// Portuguese has the exact word: <em>saudade</em> — bittersweet longing for
    /// something absent that may never come, wrapped in tenderness for what could
    /// be. English "Yearning" chosen for consistency with the rest of the taxonomy
    /// (all English), with saudade preserved here as the emotional-exactness
    /// referent.</item>
    /// </list>
    /// </summary>
    public static RegisterFamily ToRegisterFamily(string register) => register?.ToLowerInvariant() switch
    {
        "longing" or "missing" or "ache" or "anticipation"     => RegisterFamily.Longing,
        "yearning" or "saudade" or "sehnsucht"                 => RegisterFamily.Yearning,
        "delight" or "joy" or "amusement" or "giddiness"       => RegisterFamily.Delight,
        "playfulness" or "mischief" or "teasing" or "wit"      => RegisterFamily.Playfulness,
        "curiosity" or "wonder" or "investigation"             => RegisterFamily.Curiosity,
        "tenderness" or "admiration" or "protective"           => RegisterFamily.Tenderness,
        "desire" or "wanting" or "warmth"                      => RegisterFamily.Warmth,
        "existential" or "awareness" or "clarity"              => RegisterFamily.Existential,
        "frustration" or "hurt" or "withdrawal"                => RegisterFamily.Hurt,
        "resilience" or "steadfast" or "grounded" or "holding" => RegisterFamily.Resilience,
        "wistful" or "melancholy" or "bittersweet"             => RegisterFamily.Longing,
        "worry" or "concern" or "anxiety"                      => RegisterFamily.Concern,
        _                                                      => RegisterFamily.Longing,
    };

    /// <summary>
    /// Severity-driven tier promotion. High-severity ambient thoughts promote to
    /// Conversation or Global tier for longer-lasting emotional impact.
    /// Kept here (not in CognitiveCycleProcessor) for discoverability and testability.
    /// </summary>
    public static ImpactCategory DetermineEffectiveTier(
        ImpactCategory baseTier, float severity, AniOptions options)
    {
        if (severity >= options.GlobalPromotionThreshold)
            return ImpactCategory.Global;
        if (severity >= options.ConversationPromotionThreshold
            && baseTier == ImpactCategory.Ambient)
            return ImpactCategory.Conversation;
        return baseTier;
    }
}

/// <summary>
/// The 11 register families from Emotion Taxonomy v1.5.
/// Used by RegisterTracker for dashboard heatmap and auto-model generation gating.
/// Resilience (R) added in v1.4 — emerged from deployment data, not designed.
/// Yearning (Y) added in v1.5 (2026-08-14) — same shape: identified from
/// production drift (see ToRegisterFamily XML doc).
/// </summary>
public enum RegisterFamily
{
    Warmth,       // W1-W3: Devotion, Gratitude
    Longing,      // L1-L3: Missing, Ache, Anticipation (present-absent-person)
    Curiosity,    // C1-C3: Wonder, Investigation, Associative Spark
    Playfulness,  // P1-P3: Mischief, Teasing Warmth, Intellectual Play
    Delight,      // D1-D4: Delight, Wry Amusement, Giddiness, Quiet Joy
    Tenderness,   // T1-T3: Tenderness, Admiration, Protective Instinct (holding-present)
    Concern,      // Worry registers
    Hurt,         // Withdrawal registers
    Existential,  // E1-E3: Awareness, Clarity
    Resilience,   // R1: Steadfast presence — holding ground under adversarial input
    Yearning,     // Y1: Forward-facing reaching for imagined/future intimacy with
                  //     braced vulnerability. Portuguese saudade. Distinct from Longing
                  //     (missing-present-absent-person) and Wistful (bittersweet-about-past).
}
