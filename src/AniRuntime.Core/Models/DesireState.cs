using System.Text.Json.Serialization;
using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

public record DesireState
{
    public float          DesireToConnect      { get; set; }        // 0.0 – 1.0, builds over time
    public float          OutreachThreshold    { get; set; }        // randomised each evaluation
    public bool           CooldownActive       { get; set; }
    public DateTimeOffset CooldownUntil        { get; set; }        // expiry — cooldown lifts automatically
    public DateTimeOffset LastOutreach         { get; set; }
    public DateTimeOffset LastInnerThought     { get; set; }
    [JsonPropertyName("LastMarkContact")]
    public DateTimeOffset LastContactInbound   { get; set; } = DateTimeOffset.UtcNow;
    public List<DesireTrigger> ActiveTriggers  { get; set; } = new();
    public float          CircadianModifier    { get; set; } = 1.0f;
}

/// <summary>
/// A single desire trigger. Foundation Input Phase 2 (2026-08-15) makes
/// DesireTrigger implement <see cref="IActiveTriggerEnvelope"/> so the
/// outreach composer can render triggers with type-tag + semantic-dedup
/// rather than as a semicolon-joined flat description blob.
///
/// <para>
/// <see cref="SemanticKey"/> is populated by <see cref="AniRuntime.Loops.DesireEngine.AddTriggerAsync"/>
/// via the embedding model. Nullable to keep pre-Phase-2 persisted state
/// deserialisable — old rows come back with SemanticKey=null and simply
/// skip semantic dedup on first re-encounter.
/// </para>
/// </summary>
public class DesireTrigger : IActiveTriggerEnvelope
{
    public TriggerType    Type        { get; set; }
    public float          Weight      { get; set; }
    public string         Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt   { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// F-1 Phase 2 (2026-08-15): nomic-embed-text embedding of
    /// <see cref="Description"/>. Populated at AddTriggerAsync-time when
    /// the embedding transport is available; null otherwise. Consumers
    /// use this to detect near-duplicate triggers via cosine similarity.
    ///
    /// <para>
    /// INVARIANT: this key is paired with <see cref="Description"/>. On a
    /// merge, <see cref="AniRuntime.Loops.DesireEngine.AddTriggerAsync"/>
    /// leaves both fields on the surviving envelope UNCHANGED — the key is
    /// not "refreshed" to the new content's embedding, because that would
    /// let chained near-threshold merges walk the key away from the
    /// displayed content (Serge/Mark review 2026-08-15).
    /// </para>
    /// </summary>
    public float[]? SemanticKey { get; set; }

    /// <summary>
    /// F-1 Phase 2 (2026-08-16, review fix): call-site-supplied source
    /// discriminator. Two different producer sites can pass the same
    /// <see cref="TriggerType"/> for different reasons (e.g.
    /// <c>SpontaneousThought</c> from `CognitiveCyclePipeline` inner-thought
    /// valence vs. from `OutreachPipeline` held-back-outreach fallback); the
    /// <see cref="Type"/> enum alone cannot distinguish them. When this
    /// field is set, <see cref="IProvenancedContent{T}.SourceType"/> renders
    /// as <c>"trigger.{Source}"</c>; otherwise it falls back to a mapping
    /// from <see cref="Type"/>. Nullable to keep pre-Phase-2 persisted
    /// state deserialisable.
    /// </summary>
    public string? Source { get; set; }

    // ── IActiveTriggerEnvelope / IProvenancedContent<string> ────────────────
    // These are computed accessors — no additional persisted fields required.

    /// <inheritdoc />
    [JsonIgnore]
    string IProvenancedContent<string>.Content => Description;

    /// <inheritdoc />
    [JsonIgnore]
    string IProvenancedContent<string>.SourceType => !string.IsNullOrWhiteSpace(Source)
        ? $"trigger.{Source}"
        : Type switch
        {
            TriggerType.TemporalDrift      => "trigger.temporal-drift",
            TriggerType.OpenLoop           => "trigger.open-loop",
            TriggerType.AssociativeFire    => "trigger.associative-fire",
            TriggerType.EmotionalResidue   => "trigger.emotional-residue",
            TriggerType.SpontaneousThought => "trigger.spontaneous-thought",
            TriggerType.ContextualMoment   => "trigger.contextual-moment",
            TriggerType.IntegrationEvent   => "trigger.integration-event",
            TriggerType.ReactiveShare      => "trigger.reactive-share",
            _                              => "trigger.unknown",
        };

    /// <inheritdoc />
    [JsonIgnore]
    string IProvenancedContent<string>.Producer => "DesireEngine";

    /// <inheritdoc />
    [JsonIgnore]
    TriggerType IActiveTriggerEnvelope.TriggerType => Type;

    /// <inheritdoc />
    [JsonIgnore]
    float IActiveTriggerEnvelope.Weight => Weight;
}

public enum TriggerType
{
    TemporalDrift,
    OpenLoop,
    AssociativeFire,
    EmotionalResidue,
    SpontaneousThought,
    ContextualMoment,
    IntegrationEvent,
    ReactiveShare
}
