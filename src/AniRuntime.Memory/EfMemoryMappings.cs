using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Memory.Entities;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — shared entity↔domain mapping
/// helpers used by the focused EF services. Extracted from the previous
/// monolithic <c>EfMemoryService</c> so multiple focused services
/// (persistence + search + analytics) can share the same mapping
/// without duplicating it.
///
/// Static utility class — no state, no dependencies beyond the
/// entity + domain model definitions. Kept internal-friendly so test
/// fixtures can call <see cref="MapToRecord"/> directly when needed.
/// </summary>
internal static class EfMemoryMappings
{
    public static MemoryRecord MapToRecord(MemoryEntity e) => new()
    {
        Id                = e.Id,
        Type              = e.Type,
        Content           = e.Content,
        RawJson           = e.RawJson,
        Importance        = e.Importance,
        RelationalValence = e.RelationalValence,
        Embedding         = e.Embedding,
        IsResolved        = e.IsResolved,
        SourceName        = e.SourceName,
        OccurredAt        = e.OccurredAt,
        CreatedAt         = e.CreatedAt,
        ResolvedAt        = e.ResolvedAt,
        DecayTier         = e.Tier,
        AnchorReason      = e.AnchorReason,
        AnchoredAt        = e.AnchoredAt,
        Provenance        = e.Provenance,
        ConfirmedAt       = e.ConfirmedAt,
        ConfirmedBy       = e.ConfirmedBy,
        Register          = e.Register,
        // F-2 Phase 1 P2 (2026-08-21) — attribution fields
        AttributedTo               = e.AttributedTo,
        AttributedAt               = e.AttributedAt,
        AttributedSourceRecordId   = e.AttributedSourceRecordId,
        AttributedSourceDescriptor = e.AttributedSourceDescriptor,
        AttributionTrust           = e.AttributionTrust,
    };

    /// <summary>
    /// Type-aware recency multiplier matching <c>SqliteMemoryService.GetDecayMultiplier</c>.
    /// Kept in sync via the GetDecayMultiplier-test invariants — if either
    /// drifts the retrieval-eligibility predicate diverges from the
    /// production scoring.
    /// </summary>
    public static float RecencyScore(MemoryEntity entity, DateTimeOffset now, AniOptions options)
    {
        if (entity.Tier == DecayTier.Anchored) return 1.0f;

        var hoursSince = (now - entity.OccurredAt).TotalHours;
        var multiplier = entity.Type switch
        {
            MemoryType.Episodic     => 2.0f,
            MemoryType.Semantic     => 2.0f,
            MemoryType.Commitment   => 2.0f,
            MemoryType.OpenLoop     => 1.5f,
            MemoryType.InnerThought => 1.0f,
            MemoryType.Perception   => 0.5f,
            _                       => 1.0f,
        };
        var lambda = options.RetrievalRecencyDecayHours * multiplier;
        return (float)Math.Exp(-hoursSince / lambda);
    }
}
