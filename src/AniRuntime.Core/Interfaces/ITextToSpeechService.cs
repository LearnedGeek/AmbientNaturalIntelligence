using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

public interface ITextToSpeechService
{
    Task<Stream> SynthesizeAsync(string text, EmotionalState? emotionalState = null, CancellationToken ct = default);
}
