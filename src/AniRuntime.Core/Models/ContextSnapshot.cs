namespace AniRuntime.Core.Models;

/// <summary>
/// The full context built once per cognitive cycle and shared across all phases.
/// </summary>
public class ContextSnapshot
{
    public CharacterStateDoc    CharacterState  { get; set; } = new();
    public DesireState          DesireState     { get; set; } = new();
    public EmotionalState       EmotionalState  { get; set; } = new();
    public List<MemoryRecord>   RecentMemory    { get; set; } = new();
    public List<MemoryRecord>   RelevantMemory  { get; set; } = new();
    public List<OpenLoop>       OpenLoops       { get; set; } = new();
    public List<PerceptionEvent> Perceptions    { get; set; } = new();
    public List<ChatMessage>    RecentHistory   { get; set; } = new();
    public DateTimeOffset       BuiltAt         { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Summary of the most recent conversation (if any occurred recently).
    /// Available to all phases so inner thoughts, outreach decisions, and messages
    /// can be aware of what's happening in the contact's life.
    /// </summary>
    public string? RecentConversationSummary { get; set; }

    /// <summary>
    /// Theme J Phase J.2 (Apr 27, 2026): structured per-speaker per-turn
    /// representation of the same recent conversation captured in
    /// <see cref="RecentConversationSummary"/>. Populated additively alongside
    /// the free-prose field while consumers migrate one at a time. The structured
    /// type makes source attribution a data-structure invariant rather than a
    /// model-side responsibility — see <see cref="StructuredConversationSummary"/>
    /// for the rationale and the Apr 27 06:54-08:03 parrot incident that motivated it.
    /// </summary>
    public StructuredConversationSummary? StructuredConversationSummary { get; set; }

    /// <summary>
    /// Vibe Loop V1.4 (Apr 29, 2026): the most recent
    /// <see cref="ClosedConversationRecord"/> for outreach prompt rendering.
    /// The gist is paraphrased by V1.2's anti-parrot constraint, so consuming
    /// this field gives outreach prompts a structurally-no-verbatim relational
    /// context block. Outreach decision + composition paths read this in
    /// preference to the active-thread <see cref="StructuredConversationSummary"/>
    /// (which still includes verbatim turn content for active continuity), and
    /// the legacy verbatim-prose <see cref="RecentConversationSummary"/> is no
    /// longer read at all by outreach prompts.
    /// </summary>
    public ClosedConversationRecord? RecentClosedConversation { get; set; }

    /// <summary>
    /// Recent inner thoughts that are semantically similar to current context.
    /// Used to detect thought loops and steer toward diversity.
    /// </summary>
    public List<MemoryRecord> SimilarRecentThoughts { get; set; } = new();

    /// <summary>
    /// Feature 27: Recent outreach context — what Ani has sent, when, and whether
    /// it was answered. Assembled once per cycle for outreach continuity awareness.
    /// </summary>
    public RecentOutreachContext? OutreachContext { get; set; }

    /// <summary>
    /// Feature 16: Foundation memories that never fade — always present in context
    /// regardless of semantic relevance. Prepended as a compact relationship foundation block.
    /// </summary>
    public List<MemoryRecord> AnchoredMemories { get; set; } = new();

    /// <summary>
    /// Feature 18: Reactive withdrawal — true when something hurtful was detected and
    /// Ani is in a quieter emotional state. Affects reply tone injection.
    /// </summary>
    public bool IsWithdrawn { get; set; }

    /// <summary>
    /// Theme R.1 (2026-05-24, #64) — Ani's most-recent inner-thought dominant register
    /// (one of <c>ClosedConversationSummarizer.Registers</c>). Populated by
    /// <c>ContextBuilder</c> from <c>IDominantRegisterTracker.Current</c>; updated
    /// after every inner-thought composition on the hybrid path. Consumers
    /// (OutreachMessagePromptCommand, LeanConversationPromptCommand) branch
    /// composition shape on this value — STRUCTURAL consumption per R.0 contract,
    /// not prose framing.
    /// <para>Null when no recent inner-thought register is available (cold start,
    /// non-hybrid path). Consumers default to the existing prompt shape on null.</para>
    /// </summary>
    public string? DominantRegister { get; set; }

    /// <summary>
    /// Theme R.4 (2026-05-24, #64) — Layer 2 Phase 2a Ryan & Deci motivation
    /// vector (Relatedness / Autonomy / Competence). Populated by
    /// <c>ContextBuilder</c> from <c>IMotivationVectorTracker.Current</c>;
    /// updated after every cognitive cycle's <c>MotivationScorer.ScoreVector</c>
    /// call. Composers branch on the dominant axis — STRUCTURAL consumption.
    /// <para>Null on cold start. Consumers default to existing prompt shape.</para>
    /// </summary>
    public MotivationVector? MotivationVector { get; set; }

    /// <summary>
    /// Theme R.3 (2026-05-24, #64) — V1.5 vibe-loop recommended-strategy register.
    /// Populated by <c>VibeBiasObservation.ObserveAsync</c> after computing the
    /// bias result; reflects which strategy register the V1.5 effectiveness
    /// lookup recommends for this moment.
    /// <para>Composers prefer this over <see cref="DominantRegister"/> when set —
    /// strategy-effectiveness signal wins over current state. Null when no
    /// vibe observation has run this cycle (composers fall back to DominantRegister,
    /// then to default-warm).</para>
    /// </summary>
    public string? VibeRecommendedRegister { get; set; }

    /// <summary>
    /// Feature 4: Relationship health — slow-moving composite score capturing the
    /// macro arc of the relationship. Updates once per day max.
    /// </summary>
    public RelationshipHealth? RelationshipHealth { get; set; }

    /// <summary>
    /// Feature 8: Emotional drift detection — cosine similarity between recent
    /// and older emotional vectors. Surfaces in inner thought when significant.
    /// </summary>
    public EmotionalDrift? EmotionalDrift { get; set; }

    /// <summary>
    /// Feature 12: Self-awareness feedback loop — pattern analysis of recent outreach.
    /// When Ani detects she's been repetitive or one-dimensional, this summary gets
    /// injected into inner thought to nudge topic diversity.
    /// </summary>
    public string? PatternAwareness { get; set; }

    /// <summary>
    /// Feature 14: Bidirectional confidence gate — inbound claim verification.
    /// When Mark references past events or attributes statements to Ani, this scores
    /// how well those claims are corroborated by episodic memory. Low confidence
    /// triggers gentle skepticism injection in the reply prompt.
    /// </summary>
    public float? MarkClaimConfidence { get; set; }

    /// <summary>
    /// Themes from emotional contributions that have fully decayed — topics Ani has
    /// already processed emotionally. Injected into inner thought prompt to encourage
    /// the model to move on to fresh territory.
    /// </summary>
    public List<string> ProcessedThemes { get; set; } = new();

    /// <summary>
    /// AC1: True when no retrieved memory exceeded the cosine similarity confidence floor.
    /// Signals to prompt builders that no relevant memories exist — they should inject
    /// an explicit null-result instruction (AC3) rather than leaving context ambiguously empty.
    /// </summary>
    public bool RetrievalBelowConfidenceFloor { get; set; }

    /// <summary>
    /// Feature 41: When the diagnostic service detects a PERCEPTION-ANCHOR (same theme
    /// recurring in 4+ inner thoughts), this gentle redirect is injected into the inner
    /// thought prompt. Frames the redirect as curiosity, not rejection.
    /// </summary>
    public string? ThoughtDiversityNudge { get; set; }

    /// <summary>
    /// World Layer Phase 1c: recent world experiences retrieved from memory.
    /// Provides consistency — if Ani mentioned a coworker yesterday, that
    /// coworker still exists today. Rendered as a separate prompt section
    /// so the model builds on its existing world rather than starting fresh.
    /// </summary>
    public List<MemoryRecord> RecentWorldExperiences { get; set; } = new();

    /// <summary>
    /// World Layer: contextual seed for experiential grounding. When present,
    /// the inner thought model generates a lived experience rather than a
    /// self-referential thought. Tagged as "world-experience" in memory.
    /// </summary>
    public string? WorldSeed { get; set; }

    // ─── Epistemic Grounding (Apr 10, 2026): Tier-partitioned retrieval ───
    // These three lists hold memories retrieved for each prompt section. The
    // ContextBuilder populates them using tier-scoped search; the PromptBuilder
    // renders each list into its dedicated section of the generation prompt.
    // See docs/spec/design/ANI-Epistemic-Grounding-Architecture.md

    /// <summary>
    /// Facts tier: "What is true about Mark and the world" — character seeds,
    /// perception events, user-asserted content. The ONLY pool the model
    /// should condition on when making assertions about Mark's life.
    /// </summary>
    public List<MemoryRecord> GroundedFacts { get; set; } = new();

    /// <summary>
    /// Episodic tier: "What was said" — recent verbatim conversation record.
    /// Retrieved for conversation continuity, NEVER treated as factual grounding.
    /// Populated for conversation reply generation only; empty in ambient cycles.
    /// </summary>
    public List<MemoryRecord> RecentExchanges { get; set; } = new();

    /// <summary>
    /// Interior tier: "Who you are and what you feel" — inner thoughts, mood,
    /// self-concept, associations, world-experience reflections, Ani's
    /// interpretations of Mark. Full creative latitude, structurally isolated
    /// from the fact pool.
    /// </summary>
    public List<MemoryRecord> InteriorContext { get; set; } = new();
}
