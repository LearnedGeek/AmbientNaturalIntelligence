namespace LearnedGeek.ML.Models;

/// <summary>
/// Result of classifying the primary emotion in a text segment.
/// </summary>
public record EmotionResult(
    string PrimaryEmotion,
    float Confidence,
    Dictionary<string, float> Scores);

/// <summary>
/// Result of detecting sarcasm in a text segment.
/// </summary>
public record SarcasmResult(bool IsSarcastic, float Confidence);

/// <summary>
/// Result of checking whether a reply is grounded in conversation context.
/// </summary>
public record ConfabulationResult(
    bool IsConfabulated,
    float Confidence,
    string? Reason);

/// <summary>
/// Result of classifying which emotional register a text segment belongs to.
/// </summary>
public record RegisterResult(
    string PrimaryRegister,
    float Confidence,
    Dictionary<string, float> Scores);

/// <summary>
/// A named entity extracted from text (person, place, organization, etc.).
/// </summary>
public record NamedEntity(
    string Value,
    string EntityType,
    int StartIndex,
    int EndIndex);
