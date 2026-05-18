namespace AniRuntime.Voice.Abstractions;

/// <summary>
/// Pure string formatting — builds the TwiML responses that Twilio's voice
/// webhook expects. Two shapes: <c>&lt;Play&gt;</c> (with pre-synthesized
/// audio URL) and <c>&lt;Say&gt;</c> (Twilio's fallback voice, used when
/// TTS fails). Both forms end with a <c>&lt;Record&gt;</c> that POSTs the
/// next turn back to the orchestrator.
/// </summary>
public interface IVoiceTwimlBuilder
{
    string BuildPlayAndRecord(string callSid, string audioUrl);
    string BuildSayAndRecord(string callSid, string sayText);
}
