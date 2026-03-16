using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Streaming text-to-speech service. Opens a persistent WebSocket to a TTS provider,
/// accepts text chunks (from LLM token buffer), and fires events when audio is available.
/// Each instance manages one TTS session (one per voice call).
/// </summary>
public interface IStreamingTextToSpeechService : IAsyncDisposable
{
    /// <summary>Open the streaming TTS connection with optional emotional voice settings.</summary>
    Task StartAsync(EmotionalState? emotionalState = null, CancellationToken ct = default);

    /// <summary>Send a text chunk for synthesis. Audio arrives via AudioChunkReceived.</summary>
    Task SendTextAsync(string textChunk, CancellationToken ct = default);

    /// <summary>Signal end of text input and flush any remaining buffered audio.</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>Close the connection and release resources.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>Fires when audio bytes are available for playback (PCM 16kHz 16-bit mono).</summary>
    event Action<ReadOnlyMemory<byte>> AudioChunkReceived;
}
