namespace AniRuntime.MauiClient;

/// <summary>
/// Captures audio from the device microphone and streams PCM 16kHz 16-bit mono chunks.
/// </summary>
public interface IAudioCaptureService
{
    void Start();
    void Stop();

    /// <summary>Fires when a chunk of PCM audio is available (typically every 20ms).</summary>
    event Action<byte[]> AudioDataAvailable;
}
