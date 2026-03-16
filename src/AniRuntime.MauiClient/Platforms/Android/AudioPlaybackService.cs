using Android.Media;

namespace AniRuntime.MauiClient;

/// <summary>
/// Android AudioTrack wrapper. Plays PCM 16kHz, 16-bit, mono audio
/// through the device speaker. Routes to speakerphone for hands-free driving.
/// </summary>
public class AudioPlaybackService : IAudioPlaybackService
{
    private AudioTrack? _track;

    private const int SampleRate = 16000;
    private const ChannelOut Channel = ChannelOut.Mono;
    private const Encoding AudioEncoding = Encoding.Pcm16bit;

    public void Start()
    {
        var bufferSize = AudioTrack.GetMinBufferSize(SampleRate, Channel, AudioEncoding);

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
        }
    }

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
