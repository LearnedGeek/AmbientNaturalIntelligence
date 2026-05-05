namespace AniRuntime.Core;

/// <summary>
/// Well-known string constants used across the codebase.
/// Centralizes magic strings to prevent typos and enable safe refactoring.
/// </summary>
public static class Roles
{
    public const string Ani  = "ani";
    public const string Mark = "mark";
}

public static class SourceNames
{
    public const string CharacterSeed       = "character-seed";
    public const string WorldExperience     = "world-experience";
    public const string ReflectionSynthesis = "reflection";
}

/// <summary>
/// Canonical values for <c>MemoryRecord.AnchorReason</c>. The schema stores
/// AnchorReason as TEXT for forward-compatibility, but writes should prefer
/// these constants so analytics + dashboard rendering can rely on a stable
/// taxonomy.
///
/// **Why these four categories** (May 3, 2026 — added per Phase Tracker
/// gap-watch row Apr 27 motivating Paper 2 figure #4 McAdams anchored-memory
/// narrative). Anchored memories serve four distinct narrative roles:
///   - <see cref="Origin"/> — the why-Ani-exists facts (relationship origin
///     points, names, dates that pre-date the runtime).
///   - <see cref="Foundation"/> — the always-true ground state (canonical
///     character traits, persona-defining facts, world-layer canon).
///   - <see cref="ArchitecturalGrowth"/> — anchored memories that mark a
///     deliberate architecture-change inflection (e.g., "after the Mar 23
///     pipeline simplification, conversation quality improved").
///   - <see cref="LessonsAsHistory"/> — failure cases preserved as
///     teaching-via-narrative (the boats-float pattern: superseded beliefs
///     retained as "I used to think X" rather than deleted).
///
/// New writes should set <c>memory.AnchorReason</c> to one of these values.
/// Existing pre-May-3 records hold NULL until a curation pass populates them
/// from canonical sources (Paper 1, Research Log, transcript_recovery.md).
/// </summary>
public static class AnchorReasonValues
{
    public const string Origin               = "origin";
    public const string Foundation           = "foundation";
    public const string ArchitecturalGrowth  = "architectural-growth";
    public const string LessonsAsHistory     = "lessons-as-history";

    /// <summary>
    /// Validate that a string is one of the canonical four values.
    /// Returns true for null (NULL is the legitimate "not-yet-categorised"
    /// state for pre-May-3 anchored memories pending curation).
    /// </summary>
    public static bool IsValidOrNull(string? value) =>
        value is null
            or Origin or Foundation or ArchitecturalGrowth or LessonsAsHistory;

    /// <summary>All four canonical values, for dashboard rendering + tests.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Origin, Foundation, ArchitecturalGrowth, LessonsAsHistory,
    };
}

public static class MemoryPrefixes
{
    /// <summary>Prefix for conversation summary records in episodic memory.</summary>
    public const string ConversationSummary = "Conversation (";

    /// <summary>
    /// Formats an episodic memory with clear speaker attribution.
    /// Ani's own words use first person ("I said") to prevent the inner thought model
    /// from misattributing her words to the contact ("he said goodnight my king"
    /// when she was the one who said it). Contact words stay third person ("Mark said").
    /// </summary>
    public static string FormatSpeaker(string speakerRole, string characterName, string contactName, string content)
    {
        // Ani's words → first person so retrieval preserves self-attribution
        if (speakerRole == Roles.Ani)
            return $"I said to {contactName}: \"{content}\"";

        // Contact's words → third person (already clear)
        return $"{contactName} said: \"{content}\"";
    }

    /// <summary>
    /// Formats a perception of an inbound message from the contact.
    /// Always third person — these are observations about what the contact did.
    /// </summary>
    public static string FormatContactPerception(string contactName, string content)
        => $"{contactName} texted: \"{content}\"";

    /// <summary>
    /// Formats an outreach record — what Ani sent unprompted.
    /// First person to prevent misattribution in inner thought processing.
    /// </summary>
    public static string FormatOutreach(string contactName, string content)
        => $"I reached out to {contactName}: \"{content}\"";
}

/// <summary>
/// Strings the runtime emits when a gate refuses both the original output and
/// the regeneration. Surfaced here so producers (the dispatching path) and
/// consumers (the persistence path that decides whether to record output as
/// Episodic substrate) reference the same canonical text.
///
/// **Why centralised** (May 4, 2026 — Phase Tracker gap-watch row May 4
/// evening): SafeAcknowledgement was being persisted to Episodic memory at
/// dispatch and re-entering the retrieval pool. Three fall-throughs in 23
/// hours produced three "I said to Mark: 'mmm, sorry...'" Episodic records,
/// observed in J0_RETRIEVAL_TEMPORAL on the next cycle. Fall-through artifacts
/// must not become substrate. The constant moves here so the persistence path
/// can reference the same string the dispatch path emits, without coupling
/// the two layers through a string literal.
/// </summary>
public static class GateFallbacks
{
    /// <summary>
    /// Canonical safe-acknowledgement dispatched when the J.5a re-eval gate
    /// refuses both the original reply and the regeneration. Mirrors
    /// <c>ConversationReplyPhase.SafeAcknowledgement</c> by reference.
    /// </summary>
    public const string SafeAcknowledgement = "mmm, sorry — give me a second to gather my thoughts.";
}
