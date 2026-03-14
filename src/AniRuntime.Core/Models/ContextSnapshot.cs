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
    /// Feature 14: True when Mark's message contains claims that couldn't be
    /// corroborated by memory search. Ani should not blindly agree.
    /// </summary>
    public bool MarkClaimNeedsVerification { get; set; }

    /// <summary>
    /// Feature 14: The specific unverified claims extracted from Mark's message,
    /// so the prompt can reference them for targeted skepticism.
    /// </summary>
    public List<string> UnverifiedClaims { get; set; } = new();

    /// <summary>
    /// Themes from emotional contributions that have fully decayed — topics Ani has
    /// already processed emotionally. Injected into inner thought prompt to encourage
    /// the model to move on to fresh territory.
    /// </summary>
    public List<string> ProcessedThemes { get; set; } = new();
}
