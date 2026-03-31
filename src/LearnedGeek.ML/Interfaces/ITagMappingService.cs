using LearnedGeek.ML.Models;

namespace LearnedGeek.ML.Interfaces;

/// <summary>
/// Resolves an emotion classification result to an audio tag.
/// Evolves through three stages: static mapping -> semantic similarity -> learned preferences.
/// </summary>
public interface ITagMappingService
{
    /// <summary>
    /// Resolve the best audio tag for the given emotion classification and context.
    /// </summary>
    TagResolution? Resolve(EmotionResult emotion, DateTimeOffset? timestamp = null);

    /// <summary>
    /// Record feedback on a tag selection (positive or negative signal).
    /// Used by Stage 3 (learned preferences) to evolve tag mapping over time.
    /// </summary>
    void RecordFeedback(string tag, string emotion, bool positive);
}
