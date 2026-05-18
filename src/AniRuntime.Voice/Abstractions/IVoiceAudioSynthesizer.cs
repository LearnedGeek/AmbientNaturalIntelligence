namespace AniRuntime.Voice.Abstractions;

/// <summary>
/// Renders text to spoken audio for Twilio playback: TTS synthesis → media
/// cache → public URL. Pulls Ani's current emotional state for prosody so
/// callers don't have to thread state through.
/// </summary>
public interface IVoiceAudioSynthesizer
{
    /// <summary>
    /// Synthesize <paramref name="text"/> and return a public Play-able URL.
    /// Throws if synthesis fails — caller chooses the Twilio Say fallback.
    /// </summary>
    Task<string> SynthesizeAsync(string text, CancellationToken ct);
}
