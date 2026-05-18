using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Voice.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice.Internal;

/// <summary>
/// ElevenLabs → media cache → public URL. Pulls Ani's current emotional
/// state on each call so prosody reflects the most recent shifts (the
/// voice turn pipeline reads state at multiple points during one turn).
/// Throws on TTS failure — callers wrap and fall back to Twilio Say.
/// </summary>
public sealed class VoiceAudioSynthesizer : IVoiceAudioSynthesizer
{
    private readonly ITextToSpeechService _tts;
    private readonly MediaCacheService _cache;
    private readonly IStateStore _state;
    private readonly VoiceOptions _voiceOptions;

    public VoiceAudioSynthesizer(
        ITextToSpeechService tts,
        MediaCacheService cache,
        IStateStore state,
        IOptions<VoiceOptions> voiceOptions)
    {
        _tts          = tts   ?? throw new ArgumentNullException(nameof(tts));
        _cache        = cache ?? throw new ArgumentNullException(nameof(cache));
        _state        = state ?? throw new ArgumentNullException(nameof(state));
        _voiceOptions = voiceOptions.Value;
    }

    public async Task<string> SynthesizeAsync(string text, CancellationToken ct)
    {
        var emotionalState = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var audioStream = await _tts.SynthesizeAsync(text, emotionalState, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        var key = _cache.Store(ms.ToArray(), "audio/mpeg");
        return $"{_voiceOptions.PublicBaseUrl.TrimEnd('/')}/media/{key}";
    }
}
