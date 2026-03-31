using LearnedGeek.ML.Interfaces;
using LearnedGeek.ML.Models;
using LMKit.Model;
using LMKit.TextAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LearnedGeek.ML;

/// <summary>
/// LM-Kit.NET implementation of ITextClassificationService.
/// Uses local GGUF models for emotion, sarcasm, and entity classification.
/// Models are loaded lazily on first use and cached for the lifetime of the service.
/// </summary>
public sealed class LMKitClassificationService : ITextClassificationService, IDisposable
{
    private readonly MLOptions _options;
    private readonly ILogger<LMKitClassificationService> _log;
    private readonly SemaphoreSlim _modelLock = new(1, 1);

    private LM? _model;
    private EmotionDetection? _emotionDetector;
    private SarcasmDetection? _sarcasmDetector;
    private NamedEntityRecognition? _nerExtractor;
    private bool _initialized;
    private bool _disposed;

    // Extended emotion labels beyond the built-in 5 (Happiness, Anger, Sadness, Fear, Neutral)
    private static readonly string[] ExtendedEmotions =
        ["love", "curiosity", "amusement", "surprise", "disgust"];

    public LMKitClassificationService(
        IOptions<MLOptions> options,
        ILogger<LMKitClassificationService> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task<EmotionResult> ClassifyEmotionAsync(string text, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        if (_emotionDetector is null)
            return new EmotionResult("neutral", 0.5f, new Dictionary<string, float> { ["neutral"] = 0.5f });

        try
        {
            var category = await Task.Run(() => _emotionDetector.GetEmotionCategory(text), ct).ConfigureAwait(false);
            var confidence = _emotionDetector.Confidence;

            var primary = MapEmotionCategory(category);
            var scores = new Dictionary<string, float> { [primary] = confidence };

            // If confidence is low, the text may be ambiguous — try extended classification
            if (confidence < 0.60f && _model is not null)
            {
                var extended = TryExtendedClassification(text);
                if (extended is not null)
                {
                    foreach (var kvp in extended)
                        scores[kvp.Key] = kvp.Value;

                    // If an extended emotion scores higher, promote it
                    var best = scores.MaxBy(kvp => kvp.Value);
                    if (best.Value > confidence)
                        return new EmotionResult(best.Key, best.Value, scores);
                }
            }

            return new EmotionResult(primary, confidence, scores);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LMKit emotion classification failed for text ({Length} chars)", text.Length);
            return new EmotionResult("neutral", 0.5f, new Dictionary<string, float> { ["neutral"] = 0.5f });
        }
    }

    public async Task<SarcasmResult> DetectSarcasmAsync(string text, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        if (_sarcasmDetector is null)
            return new SarcasmResult(false, 0f);

        try
        {
            var isSarcastic = await Task.Run(() => _sarcasmDetector.IsSarcastic(text), ct).ConfigureAwait(false);
            var confidence = _sarcasmDetector.Confidence;

            return new SarcasmResult(isSarcastic, confidence);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LMKit sarcasm detection failed for text ({Length} chars)", text.Length);
            return new SarcasmResult(false, 0f);
        }
    }

    public async Task<ConfabulationResult> DetectConfabulationAsync(
        string reply, string conversationContext, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        if (_model is null || string.IsNullOrWhiteSpace(reply))
            return new ConfabulationResult(false, 0f, null);

        try
        {
            var categorizer = new Categorization(_model)
            {
                AllowUnknownCategory = false,
                Guidance = $"Given the following context about the speaker, classify whether the reply makes claims that contradict the known facts.\n\nContext:\n{conversationContext}",
            };

            var categories = new List<string> { "grounded", "speculative", "confabulated" };
            var descriptions = new List<string>
            {
                "The reply is consistent with the persona and conversation, or makes no factual claims about identity, work, or relationships",
                "The reply makes claims that could be true but are not confirmed or denied by the known facts",
                "The reply asserts facts that contradict the persona — invents specific details about identity, job, workplace, location, coworkers, or activities that conflict with known facts",
            };

            var bestIndex = await Task.Run(() =>
                categorizer.GetBestCategory(categories, descriptions, reply, normalize: true, ct), ct)
                .ConfigureAwait(false);

            if (bestIndex < 0)
                return new ConfabulationResult(false, 0f, null);

            var category = categories[bestIndex];
            var confidence = categorizer.Confidence;

            _log.LogDebug("Confabulation classification: {Reply} → {Category} ({Confidence:F2})",
                reply.Length > 80 ? reply[..80] + "..." : reply, category, confidence);

            return category switch
            {
                "confabulated" => new ConfabulationResult(true, confidence, $"ML classified as confabulated ({confidence:F2})"),
                "speculative" => new ConfabulationResult(false, confidence, $"ML classified as speculative ({confidence:F2})"),
                _ => new ConfabulationResult(false, confidence, null),
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LMKit confabulation classification failed");
            return new ConfabulationResult(false, 0f, null);
        }
    }

    public Task<RegisterResult> ClassifyRegisterAsync(string text, CancellationToken ct = default)
    {
        // Phase 4: Will use Categorization with ANI's register taxonomy as custom labels.
        // For now, return unknown register.
        return Task.FromResult(new RegisterResult("Unknown", 0f, new Dictionary<string, float>()));
    }

    public async Task<List<NamedEntity>> ExtractEntitiesAsync(string text, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        if (_nerExtractor is null)
            return [];

        try
        {
            var entities = await Task.Run(() => _nerExtractor.Recognize(text), ct).ConfigureAwait(false);
            return entities.Select(e => new NamedEntity(
                e.Value,
                e.EntityDefinition?.Label ?? "Unknown",
                0, // ExtractedEntity uses Occurrences, not character indices
                e.Value.Length)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LMKit NER failed for text ({Length} chars)", text.Length);
            return [];
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _modelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            _log.LogInformation("LMKit: Loading classification model (first use, may download ~770MB)...");

            _model = await Task.Run(() => LM.LoadFromModelID("lmkit-sentiment-analysis"), ct).ConfigureAwait(false);

            _emotionDetector = new EmotionDetection(_model) { NeutralSupport = true };
            _sarcasmDetector = new SarcasmDetection(_model);
            _nerExtractor = new NamedEntityRecognition(_model);

            _initialized = true;
            _log.LogInformation("LMKit: Classification model loaded successfully");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LMKit: Failed to load classification model. Classification will return defaults.");
            _initialized = true; // Don't retry on every call
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private Dictionary<string, float>? TryExtendedClassification(string text)
    {
        if (_model is null) return null;

        try
        {
            var categorizer = new Categorization(_model) { AllowUnknownCategory = true };
            var categories = ExtendedEmotions.ToList();
            var bestIndex = categorizer.GetBestCategory(categories, text, normalize: true);

            if (bestIndex < 0) return null;

            var result = new Dictionary<string, float>();
            var bestEmotion = categories[bestIndex];
            result[bestEmotion] = categorizer.Confidence;

            return result;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "LMKit extended classification failed, using base result");
            return null;
        }
    }

    private static string MapEmotionCategory(EmotionDetection.EmotionCategory category) => category switch
    {
        EmotionDetection.EmotionCategory.Happiness => "happiness",
        EmotionDetection.EmotionCategory.Anger => "anger",
        EmotionDetection.EmotionCategory.Sadness => "sadness",
        EmotionDetection.EmotionCategory.Fear => "fear",
        EmotionDetection.EmotionCategory.Neutral => "neutral",
        _ => "neutral",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model?.Dispose();
        _modelLock.Dispose();
    }
}
