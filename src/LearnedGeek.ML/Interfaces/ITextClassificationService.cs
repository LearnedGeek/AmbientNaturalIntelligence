using LearnedGeek.ML.Models;

namespace LearnedGeek.ML.Interfaces;

/// <summary>
/// Classifies text into emotions, registers, entities, and detects sarcasm/confabulation.
/// Domain-agnostic: consumers decide what to do with results (voice tags, triage, etc.).
/// </summary>
public interface ITextClassificationService
{
    Task<EmotionResult> ClassifyEmotionAsync(string text, CancellationToken ct = default);

    Task<SarcasmResult> DetectSarcasmAsync(string text, CancellationToken ct = default);

    Task<ConfabulationResult> DetectConfabulationAsync(
        string reply,
        string conversationContext,
        CancellationToken ct = default);

    Task<RegisterResult> ClassifyRegisterAsync(string text, CancellationToken ct = default);

    Task<List<NamedEntity>> ExtractEntitiesAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Extract associative anchors — the most evocative keywords or phrases from the text.
    /// Used by the inner thought pipeline to seed the next cycle with a creative fragment
    /// rather than feeding back the full thought (which creates echo chambers).
    /// </summary>
    Task<List<string>> ExtractAnchorsAsync(string text, int maxAnchors = 2, CancellationToken ct = default);
}
