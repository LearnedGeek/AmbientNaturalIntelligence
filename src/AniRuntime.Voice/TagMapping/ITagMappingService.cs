using LearnedGeek.ML.Models;

namespace AniRuntime.Voice.TagMapping;

/// <summary>
/// Maps emotion classification results to consumer-defined output tags.
///
/// 2026-05-20 — moved from <c>LearnedGeek.ML.Interfaces</c> back to ANI per
/// learnedgeek-libs Issue #2. The tag-mapping subsystem is ANI-flavored
/// (ElevenLabs v3 audio tags for voice synthesis); cross-project consumers
/// (DrOk / Infanzia) wouldn't naturally consume this interface. Keeping
/// interface + concrete + JSON co-located in ANI per the cleaner-boundary
/// resolution of Issue #2.
/// </summary>
public interface ITagMappingService
{
    TagResolution? Resolve(EmotionResult emotion, DateTimeOffset? timestamp = null);

    void RecordFeedback(string tag, string emotion, bool positive);
}
