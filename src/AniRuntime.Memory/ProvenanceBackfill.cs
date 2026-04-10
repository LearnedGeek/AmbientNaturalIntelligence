using AniRuntime.Core.Models;

namespace AniRuntime.Memory;

/// <summary>
/// Epistemic Grounding (Apr 10, 2026): Heuristic for assigning EpistemicTier provenance
/// to existing memory records based on their <see cref="MemoryRecord.SourceName"/> and
/// <see cref="MemoryRecord.Type"/>.
///
/// The heuristic reflects the architectural principle: Facts originate from the user
/// or external observation; Episodic records verbatim conversation; Interior is Ani's
/// own generated content. See docs/spec/design/ANI-Epistemic-Grounding-Architecture.md.
///
/// World-experience records route to Interior without content splitting — they contain
/// reflective elaborations that reference facts stored elsewhere in their original source
/// tiers. See the "World-Experience Routing" section of the design doc for the reasoning.
/// </summary>
public static class ProvenanceBackfill
{
    /// <summary>
    /// Determines the epistemic tier for a memory record based on its source name and type.
    /// This is the authoritative backfill heuristic — used both for migrating existing
    /// memories and as a fallback for any write path that hasn't been explicitly migrated
    /// to set Provenance at the call site.
    /// </summary>
    public static EpistemicTier ClassifyProvenance(MemoryRecord record)
    {
        return ClassifyProvenance(record.SourceName, record.Type);
    }

    /// <summary>
    /// Pure-function version for testing — takes source and type directly.
    /// </summary>
    public static EpistemicTier ClassifyProvenance(string? sourceName, MemoryType type)
    {
        // Normalize source name for matching. Null/empty source falls through to
        // type-based classification.
        var source = (sourceName ?? string.Empty).Trim().ToLowerInvariant();

        // ─── FACTS tier ────────────────────────────────────────────────────
        // Character seeds — established identity, routine, relationships
        if (source == "character-seed") return EpistemicTier.Facts;

        // User-asserted content — Mark's explicit words arriving via SMS.
        // The twilio-inbound source captures Mark's side of conversation as
        // assertions about his world. These are the highest-trust facts.
        if (source == "twilio-inbound") return EpistemicTier.Facts;

        // Perception events — external observations (time, weather, RSS, contact state).
        // These are things the system observed about the world, not content it generated.
        if (source.StartsWith("perception")) return EpistemicTier.Facts;
        if (source == "time" || source == "time-perception") return EpistemicTier.Facts;
        if (source == "weather" || source == "twilio-weather" || source == "world-weather")
            return EpistemicTier.Facts;
        if (source.StartsWith("rss")) return EpistemicTier.Facts;
        if (source == "contact-state" || source == "contact-state-perception")
            return EpistemicTier.Facts;
        if (source == "calendar" || source == "calendar-event") return EpistemicTier.Facts;

        // ─── INTERIOR tier ─────────────────────────────────────────────────
        // World-experience records are Ani's reflective elaborations on facts.
        // They reference existing Facts/Episodic content but do not originate facts.
        // See the "World-Experience Routing" section of the design doc for the
        // empirical analysis behind this routing decision.
        if (source == "world-experience") return EpistemicTier.Interior;

        // Reflection content — inner synthesis
        if (source == "reflection") return EpistemicTier.Interior;

        // Type-based routing for InnerThought — these are Ani's own generated thoughts
        // regardless of source tag. Inner thoughts that lack a source fall here too.
        if (type == MemoryType.InnerThought) return EpistemicTier.Interior;

        // ─── EPISODIC tier ─────────────────────────────────────────────────
        // Conversation records (both directions) — verbatim what was said.
        // Conversation source can tag either Mark's message or Ani's reply; both
        // are Episodic. The Facts tier picks up Mark's assertions via the twilio-inbound
        // source tag on the same content.
        if (source == "conversation") return EpistemicTier.Episodic;
        if (source == "outreach" || source == "outreach-dispatched") return EpistemicTier.Episodic;

        // ─── Default ───────────────────────────────────────────────────────
        // Unknown sources default to Episodic. This is the safest default because:
        //   1. It preserves the memory as "something that happened" in the record
        //   2. It does NOT contaminate the Facts pool
        //   3. It does NOT grant interior-tier creative latitude
        // Any unknown-source memory showing up in logs indicates a source we should
        // explicitly classify; the Episodic default keeps the system correct in the meantime.
        return EpistemicTier.Episodic;
    }
}
