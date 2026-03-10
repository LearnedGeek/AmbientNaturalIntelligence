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
/// Polls Twilio's message list API for inbound SMS sent to the Ani phone number.
///
/// Design rationale (from phase-2-design.md):
///   - Polling (not webhooks) — no Kestrel, no ngrok, fits IPerceptionSource pattern
///   - 30-60 second latency is a feature — it's her "thinking"
///   - Each new inbound message becomes a PerceptionEvent(Communication) with high ContactRelevance
///   - Creates or extends a ConversationThread via IConversationService
///   - Signals an early wake to shorten the heartbeat during conversation mode
///
/// Uses Twilio REST API directly via HttpClient (no Twilio SDK dependency in Perception layer).
/// </summary>
public sealed class TwilioInboundPerceptionSource : IPerceptionSource
{
    private readonly IConversationService                     _conversations;
    private readonly IMemoryService                           _memory;
    private readonly TwilioOptions                            _twilioOptions;
    private readonly AniOptions                               _aniOptions;
    private readonly IHttpClientFactory                       _httpFactory;
    private readonly ILogger<TwilioInboundPerceptionSource>   _log;

    /// <summary>
    /// Callback invoked when a new inbound message is detected.
    /// The heartbeat service sets this to trigger an early wake.
    /// </summary>
    public Action? OnMessageReceived { get; set; }

    private DateTimeOffset _lastPollTime = DateTimeOffset.UtcNow;
    private Task? _backgroundPollTask;

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
        ILogger<TwilioInboundPerceptionSource> log)
    {
        _conversations = conversations;
        _memory        = memory;
        _twilioOptions = twilioOptions.Value;
        _aniOptions    = aniOptions.Value;
        _httpFactory   = httpFactory;
        _log           = log;
    }

    public async Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        if (!IsEnabled) return [];

        var events = new List<PerceptionEvent>();

        try
        {
            var messages = await FetchInboundMessagesAsync(ct).ConfigureAwait(false);

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
                        InitiatedBy   = "mark",
                        StartedAt     = msg.DateSent,
                        LastMessageAt = msg.DateSent,
                    };
                    await _conversations.SaveThreadAsync(thread, ct).ConfigureAwait(false);
                    _log.LogInformation("New conversation thread started: {ThreadId}", thread.Id);
                }

                await _conversations.AddMessageAsync(thread.Id, new ConversationMessage
                {
                    Role    = "mark",
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
                    Metadata      =
                    {
                        ["threadId"] = thread.Id.ToString(),
                        ["messageSid"] = msg.Sid,
                    },
                });

                // Signal early wake — Mark is talking, she needs to respond
                OnMessageReceived?.Invoke();
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

    // ── Background polling ────────────────────────────────────────────────────

    /// <summary>
    /// Starts a lightweight background loop that polls Twilio every 30 seconds,
    /// independent of the cognitive cycle. When a new message is detected, fires
    /// OnMessageReceived to trigger an early wake — so the cognitive cycle runs
    /// within ~30 seconds of an inbound text instead of waiting for the next
    /// scheduled cycle (which could be 10+ minutes away).
    ///
    /// The background poll only peeks for new messages — it does NOT process them
    /// or update the watermark. Full processing still happens in PollAsync during
    /// the cognitive cycle.
    /// </summary>
    public void StartBackgroundPolling(CancellationToken ct)
    {
        if (!IsEnabled) return;
        _backgroundPollTask = BackgroundPollLoopAsync(ct);
    }

    private async Task BackgroundPollLoopAsync(CancellationToken ct)
    {
        _log.LogDebug("Background Twilio polling started (every 30s)");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

                var messages = await FetchInboundMessagesAsync(ct).ConfigureAwait(false);
                if (messages.Count > 0)
                {
                    _log.LogDebug("Background poll: {Count} new message(s) — triggering early wake",
                        messages.Count);
                    OnMessageReceived?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Background Twilio poll failed — will retry in 60s");
                try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        _log.LogDebug("Background Twilio polling stopped");
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

        // Oldest first so conversation threading is in chronological order
        messages.Sort((a, b) => a.DateSent.CompareTo(b.DateSent));

        if (messages.Count > 0)
            _log.LogDebug("Fetched {Count} new inbound messages from Twilio", messages.Count);

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
