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
    public const string CharacterSeed = "character-seed";
}

public static class MemoryPrefixes
{
    /// <summary>Prefix for conversation summary records in episodic memory.</summary>
    public const string ConversationSummary = "Conversation (";
}
