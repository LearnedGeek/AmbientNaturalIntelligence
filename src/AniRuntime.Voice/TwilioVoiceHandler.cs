using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// Handles Twilio Voice webhook interactions — inbound call recording → STT,
/// and outbound voice message delivery via TTS → Twilio.
///
/// Voice is additive to SMS, never blocking. If TTS/STT fails, falls back to text.
/// </summary>
public class TwilioVoiceHandler
{
    private readonly ISpeechToTextService _stt;
    private readonly ITextToSpeechService _tts;
    private readonly VoiceOptions _options;
    private readonly ILogger<TwilioVoiceHandler> _logger;

    public TwilioVoiceHandler(
        ISpeechToTextService stt,
        ITextToSpeechService tts,
        IOptions<VoiceOptions> options,
        ILogger<TwilioVoiceHandler> logger)
    {
        _stt     = stt;
        _tts     = tts;
        _options = options.Value;
        _logger  = logger;
    }

    /// <summary>
    /// Process an inbound voice recording from Twilio.
    /// Downloads audio, transcribes via Whisper, returns text for conversation pipeline.
    /// </summary>
    public async Task<string?> TranscribeInboundAsync(string recordingUrl, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient();
            var audioStream = await http.GetStreamAsync(recordingUrl, ct).ConfigureAwait(false);

            var text = await _stt.TranscribeAsync(audioStream, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("Voice: inbound transcription returned empty text");
                return null;
            }

            _logger.LogInformation("Voice: inbound transcribed → \"{Text}\"", text);
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice: inbound transcription failed — falling back to no-op");
            return null;
        }
    }

    /// <summary>
    /// Synthesize a reply as speech for outbound delivery.
    /// Returns the audio stream ready for Twilio Programmable Voice playback.
    /// Returns null if TTS fails (caller should fall back to SMS).
    /// </summary>
    public async Task<Stream?> SynthesizeOutboundAsync(
        string text,
        AniRuntime.Core.Models.EmotionalState? emotionalState = null,
        CancellationToken ct = default)
    {
        try
        {
            var audio = await _tts.SynthesizeAsync(text, emotionalState, ct).ConfigureAwait(false);
            _logger.LogInformation("Voice: outbound synthesized → {Bytes} bytes", audio.Length);
            return audio;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice: outbound synthesis failed — caller should fall back to SMS");
            return null;
        }
    }
}
