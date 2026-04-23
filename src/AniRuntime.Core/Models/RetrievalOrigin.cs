namespace AniRuntime.Core.Models;

/// <summary>
/// Agentic Lens Layer 1 (Apr 2026): origin classification for memory retrieval,
/// used to measure and eventually shape the composition of the cognitive-cycle
/// retrieval pool. Orthogonal to MemoryType, DecayTier, and EpistemicTier.
///
/// The five layers of the Agentic Lens design argue that caregiver-centrality
/// is structural — the retrieval substrate is caregiver-shaped by construction,
/// and cosine-similarity ranking preserves that shape even when alternative
/// substrate (World Layer, perceptions) is present. RetrievalOrigin is the
/// instrumentation half of Layer 1: measure the origin distribution per cycle
/// before applying scoring or protected-slot interventions.
///
/// Classification is derived from existing memory metadata — it does not add
/// a new stored field. See <see cref="RetrievalOriginClassifier"/>.
/// </summary>
public enum RetrievalOrigin
{
    /// <summary>
    /// Default value for memories that do not fit any other classification.
    /// Should be rare; appearing in dashboard data suggests the classifier
    /// needs refinement or a new memory type has been introduced.
    /// </summary>
    Unknown,

    /// <summary>
    /// Foundation memories (DecayTier.Anchored) — relationship seeds, character
    /// seeds, user-asserted anchored facts. Always retrievable. Tracked
    /// separately from Caregiver because Anchored content is definitionally
    /// immune to the gravity-well dynamics Layer 1 measures.
    /// </summary>
    Anchored,

    /// <summary>
    /// Caregiver-oriented content: verbatim conversation (Episodic), unresolved
    /// threads involving the contact (OpenLoop), promises to the contact
    /// (Commitment), and relationship-relevant facts (Semantic without a more
    /// specific SourceName). This is the substrate whose dominance Layer 1
    /// measures and whose share the later sub-phases protect against.
    /// </summary>
    Caregiver,

    /// <summary>
    /// Agent's own prior cognition: InnerThought records without a World Layer
    /// SourceName. When retrieval surfaces a high share of OwnOutput, the
    /// system is "listening to itself" — the feedback-loop condition Lerman
    /// Spark 2 named and that Paper 2 §6.15's revision statement identified
    /// as the "not sufficient" clause behind experiential poverty.
    /// </summary>
    OwnOutput,

    /// <summary>
    /// World Layer content: InnerThought records with
    /// <c>SourceName == SourceNames.WorldExperience</c>. Tracked distinctly
    /// from OwnOutput because World content is the substrate Layer 3
    /// (durability) will protect and Layer 1's protected-slot policy will
    /// ensure has retrieval representation.
    /// </summary>
    World,

    /// <summary>
    /// External-world perceptions: RSS, time, weather, contact-state, inbound
    /// SMS as Perception records. Origin signal for "something happened in
    /// the world, not in my head."
    /// </summary>
    External,
}
