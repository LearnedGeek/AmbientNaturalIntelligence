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
}
