using Android.Media;

namespace AniRuntime.MauiClient;

/// <summary>
/// Android AudioTrack wrapper. Plays PCM 16kHz, 16-bit, mono audio
/// through the device speaker. Routes to speakerphone for hands-free driving.
/// </summary>
public class AudioPlaybackService : IAudioPlaybackService
{
    private AudioTrack? _track;
    private int _bytesWritten;

    private const int SampleRate = 16000;
    private const ChannelOut Channel = ChannelOut.Mono;
    private const Encoding AudioEncoding = Encoding.Pcm16bit;

    public void Start()
    {
        // Use 4x minimum buffer to absorb network jitter and prevent underrun clicks/pops.
        // At 16kHz mono 16-bit, min is ~640 bytes; 4x gives ~80ms of buffered audio.
        var minBuffer = AudioTrack.GetMinBufferSize(SampleRate, Channel, AudioEncoding);
        var bufferSize = Math.Max(minBuffer * 4, SampleRate * 2); // at least 1 second of PCM

        _track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.VoiceCommunication)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!)
            .SetAudioFormat(new AudioFormat.Builder()
                .SetSampleRate(SampleRate)!
                .SetChannelMask(Channel)!
                .SetEncoding(AudioEncoding)!
                .Build()!)
            .SetBufferSizeInBytes(bufferSize)
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();

        _track.Play();

        // Route to speakerphone for driving safety
        SetSpeakerphone(true);
    }

    public void Stop()
    {
        try
        {
            _track?.Stop();
            _track?.Release();
        }
        catch { /* may already be stopped */ }
        finally
        {
            _track = null;
        }

        SetSpeakerphone(false);
    }

    public void Write(byte[] pcmData)
    {
        if (_track?.PlayState == PlayState.Playing)
        {
            _track.Write(pcmData, 0, pcmData.Length);
            _bytesWritten += pcmData.Length;
        }
    }

    /// <summary>
    /// Estimated milliseconds of audio remaining in the AudioTrack buffer.
    /// At 16kHz, 16-bit mono: 32,000 bytes per second of audio.
    /// </summary>
    public int EstimatedRemainingMs
    {
        get
        {
            if (_track is null) return 0;
            var playbackHead = _track.PlaybackHeadPosition; // samples played
            var totalSamples = _bytesWritten / 2; // 16-bit = 2 bytes per sample
            var remainingSamples = Math.Max(0, totalSamples - playbackHead);
            return (int)(remainingSamples * 1000.0 / SampleRate);
        }
    }

    public void ResetByteCounter() => _bytesWritten = 0;

    private static void SetSpeakerphone(bool on)
    {
        try
        {
            var context = Android.App.Application.Context;
            var audioManager = (AudioManager?)context.GetSystemService(Android.Content.Context.AudioService);
            if (audioManager is not null)
            {
                audioManager.Mode = on ? Mode.InCommunication : Mode.Normal;
#pragma warning disable CA1422 // SpeakerphoneOn is obsoleted on API 34+ but we need it for older devices
                audioManager.SpeakerphoneOn = on;
#pragma warning restore CA1422
            }
        }
        catch { /* best effort */ }
    }
}
