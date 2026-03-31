using LearnedGeek.ML.Interfaces;
using LearnedGeek.ML.Models;
using Microsoft.Extensions.Logging;

namespace LearnedGeek.ML;

/// <summary>
/// ML-powered voice tag enricher. Replaces heuristic VoiceTagEnricher
/// with emotion classification → tag mapping pipeline.
///
/// Pipeline: text → ITextClassificationService (emotion + sarcasm) → ITagMappingService → v3 tag
///
/// Falls back gracefully when classification service is unavailable.
/// </summary>
public class MLVoiceTagEnricher
{
    private readonly ITextClassificationService _classifier;
    private readonly ITagMappingService _tagMapping;
    private readonly ILogger<MLVoiceTagEnricher> _log;

    public MLVoiceTagEnricher(
        ITextClassificationService classifier,
        ITagMappingService tagMapping,
        ILogger<MLVoiceTagEnricher> log)
    {
        _classifier = classifier;
        _tagMapping = tagMapping;
        _log = log;
    }

    /// <summary>
    /// Enrich a sentence with an ML-classified v3 audio tag.
    /// Called per-sentence before sending to ElevenLabs TTS.
    /// </summary>
    /// <returns>The text prepended with an audio tag, or the original text if no tag resolved.</returns>
    public async Task<string> EnrichAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var trimmed = text.TrimStart();

        // Don't double-tag
        if (trimmed.StartsWith('[') || trimmed.StartsWith('('))
            return text;

        try
        {
            // Classify emotion and detect sarcasm in parallel
            var emotionTask = _classifier.ClassifyEmotionAsync(text, ct);
            var sarcasmTask = _classifier.DetectSarcasmAsync(text, ct);
            await Task.WhenAll(emotionTask, sarcasmTask).ConfigureAwait(false);

            var emotion = emotionTask.Result;
            var sarcasm = sarcasmTask.Result;

            // Sarcasm overrides emotion classification
            if (sarcasm.IsSarcastic && sarcasm.Confidence >= 0.50f)
            {
                emotion = new EmotionResult("sarcasm", sarcasm.Confidence,
                    new Dictionary<string, float>(emotion.Scores)
                    {
                        ["sarcasm"] = sarcasm.Confidence,
                    });
            }

            var resolution = _tagMapping.Resolve(emotion);
            if (resolution is null)
            {
                _log.LogDebug("MLVoiceTagEnricher: no tag for {Emotion} ({Confidence:F2})",
                    emotion.PrimaryEmotion, emotion.Confidence);
                return text;
            }

            _log.LogDebug("MLVoiceTagEnricher: {Text} -> {Tag} (emotion={Emotion}, confidence={Confidence:F2})",
                text.Length > 50 ? text[..50] + "..." : text,
                resolution.Tag, resolution.MatchedEmotion, resolution.EmotionConfidence);

            return $"{resolution.Tag} {text}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MLVoiceTagEnricher: classification failed, returning untagged text");
            return text;
        }
    }

    /// <summary>
    /// Record engagement feedback on the tag that was used for a sentence.
    /// Called when engagement signals are detected (laughter, long reply, etc.).
    /// </summary>
    public void RecordFeedback(TagResolution resolution, bool positive)
    {
        _tagMapping.RecordFeedback(resolution.Tag, resolution.MatchedEmotion, positive);
    }
}
