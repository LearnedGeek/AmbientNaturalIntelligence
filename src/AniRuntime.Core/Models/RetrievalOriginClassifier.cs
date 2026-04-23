using AniRuntime.Core;

namespace AniRuntime.Core.Models;

/// <summary>
/// Classifies a <see cref="MemoryRecord"/> into a <see cref="RetrievalOrigin"/>
/// by inspecting existing metadata (DecayTier, SourceName, Type). Pure function,
/// no dependencies, no stored state changes.
///
/// Agentic Lens Layer 1 Phase 1a (Apr 2026): this is the measurement half of
/// the retrieval-diversity design. Phase 1b (MMR) and Phase 1c (protected
/// slots) will consume <see cref="RetrievalOrigin"/> to change scoring and
/// backfill behavior. Phase 1a ships the classifier and the logging
/// instrumentation only — no behavior change — so the pre-intervention
/// baseline distribution can be observed and the classifier validated
/// against real data before any scoring change is made.
/// </summary>
public static class RetrievalOriginClassifier
{
    /// <summary>
    /// Classify a memory record. Classification precedence:
    ///
    ///   1. DecayTier.Anchored  → <see cref="RetrievalOrigin.Anchored"/>
    ///      (foundation memories are their own class regardless of type)
    ///
    ///   2. SourceName == "world-experience" → <see cref="RetrievalOrigin.World"/>
    ///      (World Layer elaborations are stored as InnerThought with a
    ///      SourceName discriminator; we route them to World before falling
    ///      through to the Type switch)
    ///
    ///   3. MemoryType mapping:
    ///      - Perception              → <see cref="RetrievalOrigin.External"/>
    ///      - InnerThought (not World)→ <see cref="RetrievalOrigin.OwnOutput"/>
    ///      - Episodic / Commitment /
    ///        OpenLoop / Semantic     → <see cref="RetrievalOrigin.Caregiver"/>
    ///
    /// Semantic is classified as Caregiver by default because most Semantic
    /// content in the ANI corpus is relationship-relevant (facts about the
    /// contact, things Ani has learned about him). If dashboard data shows
    /// Semantic content routinely reflects non-caregiver knowledge, this
    /// mapping can be refined by adding a SourceName discriminator for
    /// character-seed or world-fact content.
    /// </summary>
    public static RetrievalOrigin Classify(MemoryRecord record)
    {
        // Anchored takes precedence — foundation memories are their own class
        // regardless of MemoryType or SourceName. See DecayTier.Anchored and
        // Feature 16 for the anchored-memory design.
        if (record.DecayTier == DecayTier.Anchored)
            return RetrievalOrigin.Anchored;

        // World Layer content lives inside MemoryType.InnerThought with a
        // SourceName discriminator. Route it to World before the Type switch
        // so InnerThought's default fall-through doesn't mis-classify it.
        if (string.Equals(record.SourceName, SourceNames.WorldExperience,
                          System.StringComparison.Ordinal))
            return RetrievalOrigin.World;

        return record.Type switch
        {
            MemoryType.Perception   => RetrievalOrigin.External,
            MemoryType.InnerThought => RetrievalOrigin.OwnOutput,
            MemoryType.Episodic     => RetrievalOrigin.Caregiver,
            MemoryType.Commitment   => RetrievalOrigin.Caregiver,
            MemoryType.OpenLoop     => RetrievalOrigin.Caregiver,
            MemoryType.Semantic     => RetrievalOrigin.Caregiver,
            _                       => RetrievalOrigin.Unknown,
        };
    }

    /// <summary>
    /// Whether this origin class represents caregiver-oriented retrieval pull.
    /// Used by Phase 1c protected-slot logic to compute the non-caregiver
    /// share of a retrieval pool.
    /// </summary>
    public static bool IsCaregiverOriented(RetrievalOrigin origin) =>
        origin == RetrievalOrigin.Caregiver;

    /// <summary>
    /// Whether this origin represents the agent's own prior cognition.
    /// Used by Phase 1d perception source to detect feedback-loop conditions.
    /// </summary>
    public static bool IsOwnOutput(RetrievalOrigin origin) =>
        origin == RetrievalOrigin.OwnOutput;
}
