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
