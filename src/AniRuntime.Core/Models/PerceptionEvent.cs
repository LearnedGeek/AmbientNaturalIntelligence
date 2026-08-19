using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

public class PerceptionEvent : IPerceptionEnvelope
{
    public string             SourceName    { get; set; } = string.Empty;
    public PerceptionCategory Category      { get; set; }
    public string             Summary       { get; set; } = string.Empty;
    public float              ContactRelevance { get; set; }
    public DateTimeOffset     OccurredAt    { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Identifies the channel this event arrived on (e.g., "sms", "dashboard").
    /// Used by ConversationReplyPhase + IReplyChannelResolver to dispatch replies
    /// through the same channel (SRP: reply generation ≠ delivery).
    /// Null for non-communication events (weather, RSS, time).
    /// </summary>
    public string? OriginChannelId { get; set; }

    // ── IPerceptionEnvelope / IProvenancedContent<PerceptionEvent> ─────────
    // F-1 Phase 6 (2026-08-19). Zero new persisted fields — SourceName,
    // Category, OccurredAt all already present. PerceptionEvent is a class
    // (not record) so implementing the envelope has no equality-behavior
    // impact (same reasoning as Phase 5 MemoryRecord).

    /// <inheritdoc />
    /// <remarks>
    /// Envelope's Content is the full <see cref="PerceptionEvent"/>
    /// (self-reference) so consumers that need
    /// <see cref="OccurredAt"/> / <see cref="Summary"/> /
    /// <see cref="Metadata"/> can read them directly. Explicit-interface
    /// implementation keeps the self-reference out of JSON/EF surfaces
    /// (Phase 5 sibling-impl discipline).
    /// </remarks>
    PerceptionEvent IProvenancedContent<PerceptionEvent>.Content => this;

    /// <inheritdoc />
    string IProvenancedContent<PerceptionEvent>.SourceType
        => $"perception.{Category.ToString().ToLowerInvariant()}.{SourceName.ToLowerInvariant()}";

    /// <inheritdoc />
    string IProvenancedContent<PerceptionEvent>.Producer
        => string.IsNullOrEmpty(SourceName)
            ? "PerceptionSource"
            : $"PerceptionSource:{SourceName}";

    /// <inheritdoc />
    /// <remarks>
    /// Uses the existing <see cref="OccurredAt"/> field (set at
    /// construction to <c>DateTimeOffset.UtcNow</c>). Perception events
    /// are transient — they don't have a separate "created at storage"
    /// vs "occurred in world" distinction, so mapping the envelope's
    /// CreatedAt to OccurredAt is the honest reading.
    /// </remarks>
    DateTimeOffset IProvenancedContent<PerceptionEvent>.CreatedAt => OccurredAt;

    /// <inheritdoc />
    /// <remarks>
    /// Perception events don't carry embeddings pre-Phase-6 (they may
    /// gain them in a future phase for semantic-dedup at the perception
    /// boundary — Phase 2 pattern applied to perceptions). Returns null
    /// for now; downstream consumers must not assume presence.
    /// </remarks>
    float[]? IProvenancedContent<PerceptionEvent>.SemanticKey => null;

    /// <inheritdoc />
    PerceptionCategory IPerceptionEnvelope.Category => Category;
}

public enum PerceptionCategory
{
    Environment,
    Calendar,
    Content,
    Communication,
    Social,

    /// <summary>
    /// Agentic Lens Layer 1 Phase 1d (Apr 2026): a perception about the agent's
    /// own cognitive state rather than an external event. Emitted by sources like
    /// <c>RetrievalSelfDominancePerceptionSource</c> that observe patterns in
    /// recent cognition (e.g. own-output retrieval share) and surface them as
    /// interior-legible signals.
    /// </summary>
    Internal,
}
