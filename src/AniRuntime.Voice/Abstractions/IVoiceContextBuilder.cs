using AniRuntime.Core.Models;

namespace AniRuntime.Voice.Abstractions;

/// <summary>
/// Builds a lightweight <see cref="ContextSnapshot"/> for voice replies —
/// character + emotional state + anchored memories only. Skips Ollama-bound
/// work (embedding-based semantic search) so the conversation model has
/// uncontested inference time for the reply itself.
/// </summary>
public interface IVoiceContextBuilder
{
    Task<ContextSnapshot> BuildAsync(string userMessage, VoiceCallSession session, CancellationToken ct);
}
