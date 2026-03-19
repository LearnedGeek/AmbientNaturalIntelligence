using System.Net.Http.Headers;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Utilities;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// OpenAI Whisper API implementation for speech-to-text.
/// Transcribes incoming voice audio (from Twilio voice webhooks) into text
/// that enters the standard conversation pipeline.
/// </summary>
public class WhisperSpeechToTextService : ISpeechToTextService
{
    private readonly HttpClient _http;
    private readonly VoiceOptions _options;
    private readonly ILogger<WhisperSpeechToTextService> _logger;

    private const string TranscriptionUrl = "https://api.openai.com/v1/audio/transcriptions";

    public WhisperSpeechToTextService(
        HttpClient http,
        IOptions<VoiceOptions> options,
        ILogger<WhisperSpeechToTextService> logger)
    {
        _http    = http;
        _options = options.Value;
        _logger  = logger;

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.WhisperApiKey);
    }

    public async Task<string> TranscribeAsync(Stream audio, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();

        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "audio.wav");
        content.Add(new StringContent(_options.WhisperModel), "model");

        _logger.LogDebug("Whisper STT: transcribing audio ({Model})", _options.WhisperModel);

        var response = await _http.PostAsync(TranscriptionUrl, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<WhisperResponse>(json, JsonDefaults.CaseInsensitive);

        var text = result?.Text?.Trim() ?? string.Empty;
        _logger.LogInformation("Whisper STT: transcribed → {Length} chars", text.Length);

        return text;
    }

    private class WhisperResponse
    {
        public string Text { get; set; } = string.Empty;
    }
}
