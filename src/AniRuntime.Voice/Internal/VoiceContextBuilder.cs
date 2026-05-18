using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Voice.Abstractions;

namespace AniRuntime.Voice.Internal;

/// <summary>
/// Voice-tier context builder. Three SQLite reads in parallel — character
/// state, emotional state, anchored memories — and nothing else. Semantic
/// search is skipped on purpose: it'd cost a ~4s Ollama embedding call,
/// blowing the Twilio 15s turn budget and starving the reply LLM.
/// </summary>
public sealed class VoiceContextBuilder : IVoiceContextBuilder
{
    private readonly IStateStore _state;
    private readonly IMemorySearch _search;

    public VoiceContextBuilder(IStateStore state, IMemorySearch search)
    {
        _state  = state  ?? throw new ArgumentNullException(nameof(state));
        _search = search ?? throw new ArgumentNullException(nameof(search));
    }

    public async Task<ContextSnapshot> BuildAsync(string userMessage, VoiceCallSession session, CancellationToken ct)
    {
        var characterTask = _state.GetCharacterStateAsync(ct);
        var emotionalTask = _state.GetEmotionalStateAsync(ct);
        var anchoredTask  = _search.GetAnchoredMemoriesAsync(ct);
        await Task.WhenAll(characterTask, emotionalTask, anchoredTask).ConfigureAwait(false);

        return new ContextSnapshot
        {
            CharacterState   = characterTask.Result,
            EmotionalState   = emotionalTask.Result,
            RelevantMemory   = new List<MemoryRecord>(),
            AnchoredMemories = anchoredTask.Result.ToList(),
            RecentMemory     = new List<MemoryRecord>(),
            Perceptions      = new List<PerceptionEvent>(),
            OpenLoops        = new List<OpenLoop>(),
            RecentHistory    = new List<ChatMessage>(),
            BuiltAt          = DateTimeOffset.UtcNow,
        };
    }
}
