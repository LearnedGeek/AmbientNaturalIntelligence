namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Streaming speech-to-text service. Opens a persistent connection to an STT provider,
/// accepts raw audio chunks, and fires events when transcripts are available.
/// Each instance manages one STT session (one per voice call).
/// </summary>
public interface IStreamingSpeechToTextService : IAsyncDisposable
{
    /// <summary>Open the streaming STT connection.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Send a chunk of PCM 16kHz 16-bit mono audio for transcription.</summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16kHz, CancellationToken ct = default);

    /// <summary>Signal end of audio input and close the connection gracefully.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>Fires when a final transcript is available (utterance complete, ready for LLM).</summary>
    event Action<string> TranscriptReceived;

    /// <summary>Fires for interim/partial transcripts (for logging and UI display).</summary>
    event Action<string> PartialTranscriptReceived;

    /// <summary>
    /// Discard any accumulated segments that haven't fired yet (echo artifacts).
    /// Implementations without debounce logic should no-op.
    /// </summary>
    void ClearPendingSegments();

    /// <summary>
    /// Send a keep-alive signal to prevent the STT connection from going stale
    /// during periods of silence (e.g., while Ani is speaking and mic is muted).
    /// Implementations without persistent connections should no-op.
    /// </summary>
    void SendKeepAlive();
}
