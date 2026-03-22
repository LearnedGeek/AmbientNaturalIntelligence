using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Perception;

/// <summary>
/// Processes inbound SMS sent to the Ani phone number.
///
/// Inbound detection uses two complementary mechanisms:
///   - A Twilio webhook (POST /sms/inbound) receives the message instantly,
///     enqueues it locally, and fires OnMessageReceived to trigger an early wake.
///   - PollAsync (called during the cognitive cycle) drains the webhook queue first,
///     then falls back to the Twilio REST API as a safety net (e.g. if the webhook
///     was briefly unreachable). Messages are deduped by SID.
///
/// Uses Twilio REST API directly via HttpClient (no Twilio SDK dependency in Perception layer).
/// </summary>
public sealed class TwilioInboundPerceptionSource : IPerceptionSource, IChatInbound
{
    private readonly IConversationService                     _conversations;
    private readonly IMemoryService                           _memory;
    private readonly TwilioOptions                            _twilioOptions;
    private readonly AniOptions                               _aniOptions;
    private readonly IHttpClientFactory                       _httpFactory;
    private readonly ISessionNotifier                         _notifier;
    private readonly ILogger<TwilioInboundPerceptionSource>   _log;

    private DateTimeOffset _lastPollTime = DateTimeOffset.UtcNow;

    // Webhook-delivered messages waiting to be processed by the cognitive cycle
    private readonly ConcurrentQueue<TwilioMessage> _webhookQueue = new();

    public string             SourceName => "twilio-inbound";
    public PerceptionCategory Category   => PerceptionCategory.Communication;

    public bool IsEnabled =>
        _twilioOptions.InboundEnabled &&
        !string.IsNullOrWhiteSpace(_twilioOptions.AccountSid) &&
        !string.IsNullOrWhiteSpace(_twilioOptions.AuthToken) &&
        !string.IsNullOrWhiteSpace(_twilioOptions.ToNumber);

    public TwilioInboundPerceptionSource(
        IConversationService conversations,
        IMemoryService memory,
        IOptions<TwilioOptions> twilioOptions,
        IOptions<AniOptions> aniOptions,
        IHttpClientFactory httpFactory,
        ISessionNotifier notifier,
        ILogger<TwilioInboundPerceptionSource> log)
    {
        _conversations = conversations;
        _memory        = memory;
        _twilioOptions = twilioOptions.Value;
        _aniOptions    = aniOptions.Value;
        _httpFactory   = httpFactory;
        _notifier      = notifier;
        _log           = log;
    }

    /// <summary>
    /// Called by the webhook endpoint to deliver a message directly.
    /// Enqueues it for the next cognitive cycle and fires the early wake.
    /// </summary>
    public void EnqueueInbound(string messageSid, string body, DateTimeOffset receivedAt)
    {
        _webhookQueue.Enqueue(new TwilioMessage(messageSid, body, receivedAt));
        _log.LogDebug("Webhook message enqueued: {Sid}", messageSid);
        _notifier.OnMessageReceived();
    }

    /// <summary>
    /// IChatInbound implementation — dashboard chat UI sends messages here.
    /// Generates a synthetic SID with "DASHBOARD-" prefix so the reply channel
    /// resolver routes the response to the dashboard instead of SMS.
    /// </summary>
    public void EnqueueMessage(string message)
    {
        var syntheticSid = $"DASHBOARD-{Guid.NewGuid():N}";
        EnqueueInbound(syntheticSid, message, DateTimeOffset.UtcNow);
    }

    public async Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        if (!IsEnabled) return [];

        var events = new List<PerceptionEvent>();
        var seenSids = new HashSet<string>();

        try
        {
            // Primary path: drain messages delivered by the webhook
            var messages = new List<TwilioMessage>();
            while (_webhookQueue.TryDequeue(out var queued))
            {
                messages.Add(queued);
                seenSids.Add(queued.Sid);
            }

            // Safety net: also fetch from Twilio API in case the webhook missed anything
            // (e.g. ngrok was briefly down, or service restarted mid-conversation)
            var apiMessages = await FetchInboundMessagesAsync(ct).ConfigureAwait(false);
            foreach (var msg in apiMessages)
            {
                if (seenSids.Add(msg.Sid))
                    messages.Add(msg);
            }

            // Sort chronologically
            messages.Sort((a, b) => a.DateSent.CompareTo(b.DateSent));

            // Load contact name for dynamic log/event references
            var character = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);
            var contactName = character.PrimaryContactName;

            foreach (var msg in messages)
            {
                _log.LogInformation("Inbound SMS from {Contact}: {Body}", contactName, msg.Body);

                // Get or create a conversation thread
                var thread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
                if (thread is null)
                {
                    thread = new ConversationThread
                    {
                        InitiatedBy   = Roles.Mark,
                        StartedAt     = msg.DateSent,
                        LastMessageAt = msg.DateSent,
                    };
                    await _conversations.SaveThreadAsync(thread, ct).ConfigureAwait(false);
                    _log.LogInformation("New conversation thread started: {ThreadId}", thread.Id);
                }

                await _conversations.AddMessageAsync(thread.Id, new ConversationMessage
                {
                    Role    = Roles.Mark,
                    Content = msg.Body,
                    SentAt  = msg.DateSent,
                }, ct).ConfigureAwait(false);

                events.Add(new PerceptionEvent
                {
                    SourceName    = SourceName,
                    Category      = Category,
                    Summary       = $"{contactName} texted: \"{msg.Body}\"",
                    ContactRelevance = 0.95f,
                    OccurredAt    = msg.DateSent,
                    OriginChannelId = msg.Sid.StartsWith("DASHBOARD-") ? "dashboard" : "sms",
                    Metadata      =
                    {
                        ["threadId"] = thread.Id.ToString(),
                        ["messageSid"] = msg.Sid,
                    },
                });
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to poll Twilio for inbound messages");
        }

        // Also check for conversation timeout — close stale threads
        await CheckConversationTimeoutAsync(ct).ConfigureAwait(false);

        _lastPollTime = DateTimeOffset.UtcNow;
        return events;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<List<TwilioMessage>> FetchInboundMessagesAsync(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("twilio");

        // Basic auth: AccountSid:AuthToken
        var credentials = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{_twilioOptions.AccountSid}:{_twilioOptions.AuthToken}"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);

        // Twilio API: list messages sent TO our number (inbound), since last poll
        var dateSent = _lastPollTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var url = $"https://api.twilio.com/2010-04-01/Accounts/{_twilioOptions.AccountSid}" +
                  $"/Messages.json?To={Uri.EscapeDataString(_twilioOptions.FromNumber)}" +
                  $"&DateSent>={Uri.EscapeDataString(dateSent)}" +
                  $"&PageSize=10";

        var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc  = JsonDocument.Parse(json);

        var messages = new List<TwilioMessage>();

        if (doc.RootElement.TryGetProperty("messages", out var msgArray))
        {
            foreach (var msg in msgArray.EnumerateArray())
            {
                var direction = msg.GetProperty("direction").GetString();
                if (direction != "inbound") continue;

                var sid     = msg.GetProperty("sid").GetString() ?? string.Empty;
                var body    = msg.GetProperty("body").GetString() ?? string.Empty;
                var dateStr = msg.GetProperty("date_sent").GetString();

                if (string.IsNullOrWhiteSpace(body)) continue;

                var dateSentParsed = !string.IsNullOrEmpty(dateStr)
                    ? DateTimeOffset.Parse(dateStr)
                    : DateTimeOffset.UtcNow;

                // Skip messages we've already seen (before our last poll)
                if (dateSentParsed <= _lastPollTime) continue;

                messages.Add(new TwilioMessage(sid, body, dateSentParsed));
            }
        }

        if (messages.Count > 0)
            _log.LogDebug("Fetched {Count} new inbound messages from Twilio API", messages.Count);

        return messages;
    }

    private async Task CheckConversationTimeoutAsync(CancellationToken ct)
    {
        var thread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
        if (thread is null) return;

        var elapsed = DateTimeOffset.UtcNow - thread.LastMessageAt;
        if (elapsed.TotalMinutes >= _aniOptions.ConversationTimeoutMinutes)
        {
            _log.LogInformation(
                "Conversation thread {ThreadId} timed out after {Minutes:F0} min of silence",
                thread.Id, elapsed.TotalMinutes);
            await _conversations.CloseThreadAsync(thread.Id, ct).ConfigureAwait(false);
        }
    }

    private record TwilioMessage(string Sid, string Body, DateTimeOffset DateSent);
}
