using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace AniRuntime.Actions;

public class TwilioSmsAction : IAniAction
{
    private readonly TwilioOptions           _options;
    private readonly ILogger<TwilioSmsAction> _log;

    public string ActionType => ActionTypes.Sms;

    public TwilioSmsAction(IOptions<TwilioOptions> options, ILogger<TwilioSmsAction> log)
    {
        _options = options.Value;
        _log     = log;
    }

    public async Task<bool> ExecuteAsync(OutreachDecision decision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decision.Message))
        {
            _log.LogWarning("TwilioSmsAction received a decision with no message — skipping");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.AccountSid) ||
            string.IsNullOrWhiteSpace(_options.AuthToken)  ||
            string.IsNullOrWhiteSpace(_options.FromNumber)  ||
            string.IsNullOrWhiteSpace(_options.ToNumber))
        {
            _log.LogWarning("Twilio credentials not configured — logging outreach instead of sending");
            _log.LogInformation("[DRY RUN] Would have sent SMS: {Message}", decision.Message);
            return true;
        }

        TwilioClient.Init(_options.AccountSid, _options.AuthToken);

        var message = await MessageResource.CreateAsync(
            body: decision.Message,
            from: new PhoneNumber(_options.FromNumber),
            to:   new PhoneNumber(_options.ToNumber)).ConfigureAwait(false);

        var success = message.Status != MessageResource.StatusEnum.Failed;
        _log.LogInformation("Twilio SMS {Status} (Sid={Sid})", message.Status, message.Sid);
        return success;
    }
}
