using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Actions;

/// <summary>
/// Delivers replies via Twilio SMS. Used when the inbound message
/// arrived via the SMS webhook.
/// </summary>
public class SmsReplyChannel : IReplyChannel
{
    private readonly AniActionDispatcher _dispatcher;
    private readonly ILogger<SmsReplyChannel> _log;

    public string ChannelId => "sms";

    public SmsReplyChannel(AniActionDispatcher dispatcher, ILogger<SmsReplyChannel> log)
    {
        _dispatcher = dispatcher;
        _log = log;
    }

    public async Task SendReplyAsync(string message, CancellationToken ct = default)
    {
        _log.LogInformation("Dispatching sms: {Reply}",
            message.Length > 80 ? message[..80] : message);

        // F-1 Phase 8d: producer-side wrap for provenance. Envelope is
        // constructed here for the SourceType tag ("outreach-decision.sms-reply")
        // and unwrapped immediately at the dispatcher call — the dispatcher
        // signature continues to consume the bare OutreachDecision record
        // (see IOutreachDecisionEnvelope XML doc for scope rationale).
        var envelope = new OutreachDecisionEnvelope
        {
            Decision = new OutreachDecision
            {
                ShouldReach = true,
                Message     = message,
                ActionType  = ActionTypes.Sms,
                Reasoning   = "conversation reply",
            },
            Source = OutreachDecisionSource.SmsReply,
        };
        await _dispatcher.DispatchAsync(envelope.Decision, ct).ConfigureAwait(false);
    }
}
