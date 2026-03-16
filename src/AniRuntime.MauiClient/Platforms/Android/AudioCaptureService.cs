using Android.Media;

namespace AniRuntime.MauiClient;

/// <summary>
/// Android AudioRecord wrapper. Captures PCM 16kHz, 16-bit, mono audio
/// and streams chunks via event for WebSocket transmission.
/// </summary>
public class AudioCaptureService : IAudioCaptureService
{
    private AudioRecord? _recorder;
    private Thread? _captureThread;
    private volatile bool _isRecording;

    private const int SampleRate = 16000;
    private const ChannelIn Channel = ChannelIn.Mono;
    private const Encoding AudioEncoding = Encoding.Pcm16bit;

    // 20ms chunks at 16kHz, 16-bit mono = 640 bytes
    private const int ChunkSizeBytes = SampleRate * 2 * 20 / 1000;

    public event Action<byte[]>? AudioDataAvailable;

    public void Start()
    {
        if (_isRecording) return;

        var bufferSize = Math.Max(
            AudioRecord.GetMinBufferSize(SampleRate, Channel, AudioEncoding),
            ChunkSizeBytes * 4);

        _recorder = new AudioRecord(
            AudioSource.VoiceCommunication, // optimized for voice, includes echo cancellation
            SampleRate, Channel, AudioEncoding, bufferSize);

        if (_recorder.State != State.Initialized)
        {
            _recorder.Release();
            _recorder = null;
            throw new InvalidOperationException("AudioRecord failed to initialize");
        }

        _isRecording = true;
        _recorder.StartRecording();

        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "AudioCapture" };
        _captureThread.Start();
    }

    public void Stop()
    {
        _isRecording = false;

        try
        {
            _recorder?.Stop();
            _recorder?.Release();
        }
        catch { /* may already be stopped */ }
        finally
        {
            _recorder = null;
        }

        _captureThread?.Join(1000);
        _captureThread = null;
    }

    private void CaptureLoop()
    {
        var buffer = new byte[ChunkSizeBytes];

        while (_isRecording && _recorder?.RecordingState == RecordState.Recording)
        {
            var bytesRead = _recorder.Read(buffer, 0, buffer.Length);
            if (bytesRead > 0)
            {
                var chunk = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                AudioDataAvailable?.Invoke(chunk);
            }
        }
    }
}
