namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) contract: any content that crosses a producer→composer
/// boundary must carry provenance so the downstream can distinguish it from
/// content of a different type/source. Introduced 2026-08-15 as part of F-1
/// Phase 2 (see <see cref="IActiveTriggerEnvelope"/> for the first implementation).
///
/// <para>
/// The generic parameter <typeparamref name="T"/> is the WRAPPED payload
/// (e.g. <c>string</c> description, <c>MemoryRecord</c>, <c>PerceptionEvent</c>).
/// Consumers should read <see cref="Content"/> for the payload and use
/// <see cref="SourceType"/> / <see cref="Producer"/> / <see cref="SemanticKey"/>
/// to make routing, deduplication, or rendering decisions.
/// </para>
///
/// <para>
/// See <c>ani-docs/spec/ANI-Foundation-Input-Refactor-Plan.md</c> for the
/// producer-boundary discipline this interface enforces, and
/// <c>ani-docs/research/ANI-Composer-Input-Provenance-Audit-2026-08-13.md</c>
/// for the empirical audit that motivated the introduction.
/// </para>
/// </summary>
/// <typeparam name="T">The wrapped payload type.</typeparam>
public interface IProvenancedContent<out T>
{
    /// <summary>The wrapped payload.</summary>
    T Content { get; }

    /// <summary>
    /// Discriminator identifying the CLASS of content within a producer.
    /// Convention: dotted lowercase, producer-prefixed, e.g.
    /// <c>"trigger.spontaneous-thought"</c>, <c>"gist.substrate"</c>,
    /// <c>"retrieval.mark-fact"</c>. Used by consumers for type-tagged
    /// rendering and by orchestrators for section-vs-type validation.
    /// </summary>
    string SourceType { get; }

    /// <summary>
    /// The producer that emitted this envelope. Convention: PascalCase class
    /// name of the producing service, e.g. <c>"DesireEngine"</c>,
    /// <c>"InnerThoughtPhase"</c>, <c>"RetrievalContextBuilder"</c>.
    /// </summary>
    string Producer { get; }

    /// <summary>UTC timestamp when the envelope was created by the producer.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Optional semantic-similarity key (embedding vector). Populated by
    /// producers that support content-dedup. Null when embedding was
    /// unavailable (transport failure) or intentionally not computed for
    /// this envelope class. Consumers use cosine similarity against this
    /// vector to detect near-duplicates.
    /// </summary>
    float[]? SemanticKey { get; }
}
