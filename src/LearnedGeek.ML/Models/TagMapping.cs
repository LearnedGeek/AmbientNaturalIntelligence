namespace LearnedGeek.ML.Models;

/// <summary>
/// A single rule mapping an emotion classification + context to a v3 audio tag.
/// </summary>
public class TagMappingRule
{
    public required string Emotion { get; init; }
    public float MinConfidence { get; init; }
    public float MaxConfidence { get; init; } = 1.0f;
    public int? HourStart { get; init; }
    public int? HourEnd { get; init; }
    public required string Tag { get; init; }
    public int Priority { get; init; }
}

/// <summary>
/// The full static tag map loaded from JSON.
/// </summary>
public class StaticTagMap
{
    public List<TagMappingRule> Rules { get; init; } = [];
}

/// <summary>
/// Result of resolving an audio tag for a text segment.
/// </summary>
public record TagResolution(
    string Tag,
    string Source,
    string MatchedEmotion,
    float EmotionConfidence);
