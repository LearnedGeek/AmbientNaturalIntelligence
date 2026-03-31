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
}
