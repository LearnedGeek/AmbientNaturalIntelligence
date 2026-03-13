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
    private readonly TwilioOptions            _options;
    private readonly IMediaEnrichmentService? _enrichment;
    private readonly ILogger<TwilioSmsAction> _log;

    public string ActionType => ActionTypes.Sms;

    public TwilioSmsAction(
        IOptions<TwilioOptions> options,
        ILogger<TwilioSmsAction> log,
        IMediaEnrichmentService? enrichment = null)
    {
        _options    = options.Value;
        _log        = log;
        _enrichment = enrichment;
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

        // Media enrichment — voice audio, images, etc. (if service is registered)
        if (_enrichment is not null)
        {
            try
            {
                var enrichedUrls = await _enrichment.EnrichAsync(decision, ct).ConfigureAwait(false);
                decision.MediaUrls.AddRange(enrichedUrls);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Media enrichment failed — sending text only");
            }
        }

        TwilioClient.Init(_options.AccountSid, _options.AuthToken);

        var mediaUrls = decision.MediaUrls.Count > 0
            ? decision.MediaUrls.Select(u => u).ToList()
            : null;

        var message = await MessageResource.CreateAsync(
            body: decision.Message,
            from: new PhoneNumber(_options.FromNumber),
            to:   new PhoneNumber(_options.ToNumber),
            mediaUrl: mediaUrls).ConfigureAwait(false);

        var success = message.Status != MessageResource.StatusEnum.Failed;
        var mediaInfo = mediaUrls?.Count > 0 ? $", {mediaUrls.Count} media" : "";
        _log.LogInformation("Twilio SMS {Status} (Sid={Sid}{Media})", message.Status, message.Sid, mediaInfo);
        return success;
    }
}
