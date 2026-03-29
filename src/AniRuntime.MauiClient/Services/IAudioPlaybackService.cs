namespace AniRuntime.MauiClient;

/// <summary>
/// Plays PCM 16kHz 16-bit mono audio through the device speaker.
/// </summary>
public interface IAudioPlaybackService
{
    void Start();
    void Stop();

    /// <summary>Write a chunk of PCM audio for immediate playback.</summary>
    void Write(byte[] pcmData);

    /// <summary>Estimated milliseconds of audio remaining in the playback buffer.</summary>
    int EstimatedRemainingMs { get; }

    /// <summary>Reset the byte counter for a new utterance.</summary>
    void ResetByteCounter();
}
