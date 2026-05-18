using AniRuntime.Core;
using AniRuntime.Voice.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice.Internal;

/// <summary>
/// Pure string formatting for Twilio voice webhooks. No I/O, no LLM, no
/// state — just two TwiML shapes and an XML escape helper. Record settings
/// (max length / timeout) come from <see cref="VoiceOptions"/>.
/// </summary>
public sealed class VoiceTwimlBuilder : IVoiceTwimlBuilder
{
    private readonly VoiceOptions _voiceOptions;

    public VoiceTwimlBuilder(IOptions<VoiceOptions> voiceOptions)
    {
        _voiceOptions = voiceOptions.Value;
    }

    public string BuildPlayAndRecord(string callSid, string audioUrl) =>
        $"""
        <Response>
            <Play>{audioUrl}</Play>
            <Pause length="1"/>
            <Record maxLength="{_voiceOptions.VoiceRecordMaxSeconds}" action="/voice/turn?callSid={callSid}" playBeep="false" timeout="{_voiceOptions.VoiceRecordTimeoutSeconds}" />
        </Response>
        """;

    public string BuildSayAndRecord(string callSid, string sayText) =>
        $"""
        <Response>
            <Say voice="alice">{EscapeXml(sayText)}</Say>
            <Pause length="1"/>
            <Record maxLength="{_voiceOptions.VoiceRecordMaxSeconds}" action="/voice/turn?callSid={callSid}" playBeep="false" timeout="{_voiceOptions.VoiceRecordTimeoutSeconds}" />
        </Response>
        """;

    private static string EscapeXml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
}
