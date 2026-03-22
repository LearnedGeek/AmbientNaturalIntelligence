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

        var decision = new OutreachDecision
        {
            ShouldReach = true,
            Message = message,
            ActionType = ActionTypes.Sms,
            Reasoning = "conversation reply",
        };
        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);
    }
}
