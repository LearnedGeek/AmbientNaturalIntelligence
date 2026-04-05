using System.Text.Json;
using AniRuntime.Core.Utilities;
using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using AniRuntime.Memory;
using AniRuntime.Perception;
using AniRuntime.Dashboard;
using AniRuntime.Emergence;
using AniRuntime.Emergence.Models;
using AniRuntime.Voice;
using LearnedGeek.ML;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Serilog;
using Twilio.Security;

// ── Logging ───────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/ani-.log", rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddWindowsService(options => options.ServiceName = "AniRuntime");

    builder.Services.AddSerilog((services, config) => config
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}")
        // Journal — inner thoughts, outreach decisions, messages sent (queryable story)
        // No {Exception} — stack traces go to debug log only, journal stays readable
        .WriteTo.File("logs/ani-.log",
            rollingInterval: RollingInterval.Day,
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
            outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}",
            retainedFileCountLimit: 30)
        // Diagnostic — everything, for debugging
        .WriteTo.File("logs/ani-debug-.log",
            rollingInterval: RollingInterval.Day,
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug,
            outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
            retainedFileCountLimit: 7));

    var config = builder.Configuration;

    // ── Options (deferred reads — all resolved after Build()) ─────────────────
    builder.Services.Configure<AniOptions>(config.GetSection("Ani"));
    builder.Services.Configure<OllamaOptions>(config.GetSection("Ollama"));
    builder.Services.Configure<TwilioOptions>(config.GetSection("Twilio"));
    builder.Services.Configure<RssOptions>(config.GetSection("Rss"));
    builder.Services.Configure<WeatherOptions>(config.GetSection("Weather"));
    builder.Services.Configure<VoiceOptions>(config.GetSection("Voice"));
    builder.Services.Configure<ImageOptions>(config.GetSection("Images"));
    builder.Services.Configure<EmergenceOptions>(config.GetSection("Emergence"));
    builder.Services.Configure<DiagnosticOptions>(config.GetSection("Diagnostic"));

    // ── Core services ─────────────────────────────────────────────────────────
    // ISP: Single SqliteMemoryService instance registered against all interfaces.
    // Consumers should depend on the narrowest interface they need.
    builder.Services.AddSingleton<SqliteMemoryService>();
    builder.Services.AddSingleton<IMemoryService>(sp => sp.GetRequiredService<SqliteMemoryService>());
    builder.Services.AddSingleton<IMemoryPersistence>(sp => sp.GetRequiredService<SqliteMemoryService>());
    builder.Services.AddSingleton<IMemorySearch>(sp => sp.GetRequiredService<SqliteMemoryService>());
    builder.Services.AddSingleton<IStateStore>(sp => sp.GetRequiredService<SqliteMemoryService>());
    builder.Services.AddSingleton<IMemoryAnalytics>(sp => sp.GetRequiredService<SqliteMemoryService>());
    builder.Services.AddSingleton<IMemoryMaintenance>(sp => sp.GetRequiredService<SqliteMemoryService>());
    builder.Services.AddSingleton<IConversationService, SqliteConversationService>();
    builder.Services.AddSingleton<DesireEngine>();

    // ── LLM ───────────────────────────────────────────────────────────────────
    builder.Services.AddHttpClient<IOllamaClient, OllamaClient>((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.Timeout     = TimeSpan.FromMinutes(2);
    });

    // ── Voice & Media (Feature 20) — conditional on Voice:Enabled ─────────────
    var voiceEnabled = config.GetValue<bool>("Voice:Enabled");
    builder.Services.AddSingleton<MediaCacheService>();

    if (voiceEnabled)
    {
        // Batch voice (Twilio Record webhooks)
        builder.Services.AddHttpClient<ITextToSpeechService, ElevenLabsTextToSpeechService>();
        builder.Services.AddHttpClient<ISpeechToTextService, WhisperSpeechToTextService>();
        builder.Services.AddSingleton<TwilioVoiceHandler>();
        builder.Services.AddSingleton<VoiceMediaEnrichmentService>();
        builder.Services.AddSingleton<VoiceConversationService>();

        // Streaming voice (MAUI app WebSocket — Phase 5)
        var streamingEnabled = config.GetValue<bool>("Voice:StreamingEnabled");
        if (streamingEnabled)
        {
            builder.Services.AddTransient<IStreamingSpeechToTextService, DeepgramStreamingSTTService>();
            // v3 HTTP streaming replaces v2 WebSocket — supports audio tags
            builder.Services.AddTransient<IStreamingTextToSpeechService, ElevenLabsV3StreamingService>();
            builder.Services.AddSingleton<VoiceTurnPipeline>();
            builder.Services.AddSingleton<StreamingVoiceOrchestrator>();
        }
    }

    // ── Image sharing (Phase 5a) — conditional on Images:Enabled ─────────────
    var imagesEnabled = config.GetValue<bool>("Images:Enabled");
    if (imagesEnabled)
    {
        builder.Services.AddSingleton<IImageSelectionService, ImageSelectionService>();
        builder.Services.AddSingleton<ImageMediaEnrichmentService>();
    }

    // ── Composite media enrichment — assembles voice + image enrichments ──────
    builder.Services.AddSingleton<IMediaEnrichmentService>(sp =>
    {
        var enrichments = new List<IMediaEnrichmentService>();
        var voice = sp.GetService<VoiceMediaEnrichmentService>();
        if (voice is not null) enrichments.Add(voice);
        var image = sp.GetService<ImageMediaEnrichmentService>();
        if (image is not null) enrichments.Add(image);
        return new CompositeMediaEnrichmentService(enrichments);
    });

    // ── Actions ───────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<AniActionDispatcher>();
    builder.Services.AddSingleton<IAniAction, TwilioSmsAction>();
    builder.Services.AddSingleton<IAniAction, MemoryWriteAction>();

    // ── Perception sources ────────────────────────────────────────────────────
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IPerceptionSource, TimePerceptionSource>();
    builder.Services.AddHttpClient("rss");
    builder.Services.AddSingleton<IPerceptionSource, RssPerceptionSource>();
    builder.Services.AddSingleton<IPerceptionSource, ContactStatePerceptionSource>();
    builder.Services.AddHttpClient("weather");
    builder.Services.AddSingleton<IPerceptionSource, WeatherPerceptionSource>();
    builder.Services.AddHttpClient("twilio");
    builder.Services.AddHttpClient("elevenlabs-v3");
    builder.Services.AddSingleton<TwilioInboundPerceptionSource>();
    builder.Services.AddSingleton<IPerceptionSource>(sp => sp.GetRequiredService<TwilioInboundPerceptionSource>());
    builder.Services.AddSingleton<IChatInbound>(sp => sp.GetRequiredService<TwilioInboundPerceptionSource>());
    // builder.Services.AddSingleton<IPerceptionSource, HomeAssistantSource>();
    // builder.Services.AddSingleton<IPerceptionSource, CalendarPerceptionSource>();

    // ── Emergence layer (E1 — passive observation) ────────────────────────────
    var emergenceEnabled = config.GetValue<bool>("Emergence:Enabled");
    builder.Services.AddEmergence(emergenceEnabled);

    // ── Dashboard (Blazor Server + REST API) ──────────────────────────────────
    builder.Services.AddDashboard();

    // ── Reply channels (SRP: reply generation ≠ delivery) ──────────────────────
    builder.Services.AddSingleton<IReplyChannel, AniRuntime.Actions.SmsReplyChannel>();
    builder.Services.AddSingleton<IReplyChannel, AniRuntime.Dashboard.DashboardReplyChannel>();
    builder.Services.AddSingleton<IReplyChannelResolver, ReplyChannelResolver>();

    // ── World Layer — experiential grounding for inner life ─────────────────
    builder.Services.Configure<WorldSeedOptions>(config.GetSection("WorldSeed"));
    builder.Services.AddSingleton<WorldSeedService>();

    // ── Cognitive cycle ───────────────────────────────────────────────────────
    builder.Services.AddSingleton<AdminCommandHandler>();
    builder.Services.AddSingleton<EmotionalProcessor>();
    builder.Services.AddSingleton<ContextBuilder>();
    builder.Services.AddSingleton<AniRuntime.LLM.ContextCompressor>();
    builder.Services.AddSingleton<AniRuntime.LLM.KeywordExtractor>();
    builder.Services.AddSingleton<IIntentExtractor, AniRuntime.LLM.IntentExtractor>();
    builder.Services.AddSingleton<IConversationGateState, ConversationGateState>();
    builder.Services.AddSingleton<PerceptionPhase>();
    builder.Services.AddSingleton<InnerThoughtPhase>();
    builder.Services.AddSingleton<ConversationReplyPhase>();
    builder.Services.AddSingleton<OutreachPhase>();
    builder.Services.AddSingleton<ReflectionPhase>();
    builder.Services.AddSingleton<CognitiveCycleProcessor>();
    // S6: SessionNotifier is a lightweight singleton with no dependencies — breaks the
    // circular DI chain. AniHeartbeatService wires itself as the handler in its constructor.
    builder.Services.AddSingleton<SessionNotifier>();
    builder.Services.AddSingleton<ISessionNotifier>(sp => sp.GetRequiredService<SessionNotifier>());
    builder.Services.AddHostedService<AniHeartbeatService>();

    // ── Feature 41: Diagnostic service — automated log scanning ──────────────
    builder.Services.AddSingleton<IDiagnosticService, DiagnosticService>();
    builder.Services.AddHostedService<DiagnosticScheduler>();

    // ── LearnedGeek.ML — LM-Kit emotion/sarcasm classification ─────────────
    builder.Services.Configure<MLOptions>(config.GetSection("LMKit"));
    builder.Services.AddLearnedGeekML();

    // ── H3: Rate limiting — prevent webhook flooding ──────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.AddFixedWindowLimiter("webhook", limiter =>
        {
            limiter.PermitLimit = 20;           // 20 requests per window
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;             // reject immediately, don't queue
        });
        options.RejectionStatusCode = 429;
    });

    // ── Forwarded headers — needed for Twilio signature validation behind ngrok
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto |
                                   ForwardedHeaders.XForwardedHost;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });

    var app = builder.Build();

    app.UseForwardedHeaders();

    // H5: Security headers — OWASP recommended
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["X-XSS-Protection"] = "1; mode=block";
        ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        await next();
    });

    app.UseRateLimiter();
    app.UseWebSockets();
    app.UseStaticFiles();

    // ── Health endpoint — verify Ollama connectivity and SQLite accessibility ──
    app.MapGet("/health", async (IStateStore state, HttpContext ctx) =>
    {
        try
        {
            // Verify SQLite is accessible by reading character state
            var charState = await state.GetCharacterStateAsync(ctx.RequestAborted).ConfigureAwait(false);

            // Verify Ollama is reachable
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var ollamaUrl = app.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
            var ollamaResponse = await http.GetAsync($"{ollamaUrl}/api/tags", ctx.RequestAborted).ConfigureAwait(false);

            return Results.Ok(new
            {
                status = "healthy",
                sqlite = "ok",
                ollama = ollamaResponse.IsSuccessStatusCode ? "ok" : "degraded",
                character = charState.Name,
                timestamp = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new
            {
                status = "unhealthy",
                error = ex.Message,
                timestamp = DateTimeOffset.UtcNow,
            }, statusCode: 503);
        }
    });

    // ── Dashboard — REST API endpoints + Blazor Server ────────────────────
    app.MapDashboard();

    // S6: ISessionNotifier wiring is now handled via DI (registered as singleton below).
    // Voice services and perception sources inject ISessionNotifier directly instead of
    // fragile Action callbacks wired via service locator.

    // ── Inbound SMS webhook ──────────────────────────────────────────────────
    // Twilio POSTs here when an SMS arrives at Ani's number.
    // Enqueues the message for the cognitive cycle and triggers an early wake.
    // H3: Rate-limited to 20 requests/minute to prevent flooding.
    app.MapPost("/sms/inbound", async (
        HttpContext ctx,
        IOptions<TwilioOptions> twilioOpts,
        TwilioInboundPerceptionSource source) =>
    {
        var form = await ctx.Request.ReadFormAsync();

        // Validate Twilio request signature
        var authToken = twilioOpts.Value.AuthToken;
        var signature = ctx.Request.Headers["X-Twilio-Signature"].FirstOrDefault() ?? "";
        var requestUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.Path}";
        var parameters = form.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

        if (string.IsNullOrWhiteSpace(authToken))
        {
            Log.Warning("Twilio AuthToken not configured — rejecting inbound SMS webhook. Set Twilio:AuthToken in appsettings.");
            return Results.StatusCode(403);
        }

        var validator = new RequestValidator(authToken);
        if (!validator.Validate(requestUrl, parameters, signature))
        {
            Log.Warning("Rejected inbound SMS webhook — invalid Twilio signature");
            return Results.StatusCode(403);
        }

        var body = form["Body"].ToString();
        var messageSid = form["MessageSid"].ToString();

        // MMS image handling — if Mark sends a photo, process in background.
        // LLaVA needs 20-30s+ which exceeds Twilio's webhook timeout.
        // Enqueue the text immediately, then enqueue the image description when ready.
        var numMedia = int.TryParse(form["NumMedia"].ToString(), out var nm) ? nm : 0;
        if (numMedia > 0)
        {
            var mediaUrl = form["MediaUrl0"].ToString();
            var mediaType = form["MediaContentType0"].ToString();
            if (!string.IsNullOrWhiteSpace(mediaUrl) && mediaType.StartsWith("image/"))
            {
                Log.Information("Webhook: MMS image received ({Type}), queuing LLaVA description...", mediaType);
                var imgAccountSid = twilioOpts.Value.AccountSid;
                var imgAuthToken = twilioOpts.Value.AuthToken;
                var appCt = app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

                // Process image in background, but combine with text body when done.
                // Don't enqueue text separately — wait for image so Ani sees both together.
                var capturedBody = body;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var imgClient = new HttpClient();
                        var twilioAuth = Convert.ToBase64String(
                            System.Text.Encoding.ASCII.GetBytes($"{imgAccountSid}:{imgAuthToken}"));
                        imgClient.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", twilioAuth);
                        var imageBytes = await imgClient.GetByteArrayAsync(mediaUrl, appCt);

                        var ollama = app.Services.GetRequiredService<IOllamaClient>();
                        var description = await ollama.DescribeImageAsync(imageBytes, ct: appCt);

                        // Combine image description with text body as one message
                        var imageContext = $"[Mark sent a photo: {description}]";
                        var combined = string.IsNullOrWhiteSpace(capturedBody)
                            ? imageContext
                            : $"{imageContext} {capturedBody}";
                        source.EnqueueInbound(messageSid, combined, DateTimeOffset.UtcNow);
                        Log.Information("Webhook: image + text enqueued together — {Description}",
                            description.Length > 80 ? description[..80] + "..." : description);
                    }
                    catch (Exception imgEx)
                    {
                        Log.Warning(imgEx, "Webhook: failed to describe MMS image — enqueuing text with notice");
                        var fallback = string.IsNullOrWhiteSpace(capturedBody)
                            ? "[Mark sent a photo but I couldn't see it clearly. Ask him about it instead of guessing.]"
                            : $"[Mark sent a photo but I couldn't see it clearly. Ask him about it instead of guessing.] {capturedBody}";
                        source.EnqueueInbound(messageSid, fallback, DateTimeOffset.UtcNow);
                    }
                });
                // Skip the normal text enqueue below — image task handles it
                body = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            // Enqueue the message and trigger early wake — the cognitive cycle
            // will process it immediately via PollAsync draining the queue
            source.EnqueueInbound(messageSid, body, DateTimeOffset.UtcNow);
            Log.Information("Webhook: inbound SMS enqueued ({Sid})", messageSid);
        }

        // Empty TwiML — no auto-reply, Ani will respond via the cognitive cycle
        return Results.Content("<Response></Response>", "application/xml");
    }).RequireRateLimiting("webhook");

    // ── Media serving endpoint — Twilio fetches audio/images from here ─────────
    if (voiceEnabled)
    {
        app.MapGet("/media/{key}", (string key) =>
        {
            var cache = app.Services.GetRequiredService<MediaCacheService>();
            var entry = cache.Get(key);
            if (entry is null)
                return Results.NotFound();

            return Results.File(entry.Data, entry.ContentType);
        });
    }

    // ── Voice conversation loop (Feature 20) ────────────────────────────────────
    // Three endpoints: /voice/inbound (greeting + first record), /voice/turn (each
    // subsequent turn), /voice/status (call ended cleanup). Turn-by-turn: speak →
    // record → transcribe → LLM → synthesize → play → record → repeat.
    if (voiceEnabled)
    {
        // Voice endpoints use ApplicationStopping instead of ctx.RequestAborted.
        // Twilio closes webhook connections on its own timeout (~15s), which fires
        // ctx.RequestAborted and cancels in-flight HTTP calls to ElevenLabs/Whisper.
        // Voice work must complete regardless — the reply, TTS, and buffered messages
        // all matter even if Twilio's connection dropped.
        var voiceAppCt = app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

        app.MapPost("/voice/inbound", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var callSid = form["CallSid"].ToString();

            Log.Information("Voice inbound: call {CallSid}", callSid);

            var voiceService = app.Services.GetRequiredService<VoiceConversationService>();
            var twiml = await voiceService.StartCallAsync(callSid, voiceAppCt);
            return Results.Content(twiml, "application/xml");
        });

        app.MapPost("/voice/turn", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var callSid = ctx.Request.Query["callSid"].ToString();
            var recordingUrl = form["RecordingUrl"].ToString();

            if (string.IsNullOrWhiteSpace(callSid) || string.IsNullOrWhiteSpace(recordingUrl))
            {
                Log.Warning("Voice turn: missing callSid or recordingUrl");
                return Results.Content("<Response><Hangup/></Response>", "application/xml");
            }

            var voiceService = app.Services.GetRequiredService<VoiceConversationService>();
            var twiml = await voiceService.ProcessTurnAsync(callSid, recordingUrl, voiceAppCt);
            return Results.Content(twiml, "application/xml");
        });

        app.MapPost("/voice/status", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var callSid = form["CallSid"].ToString();
            var callStatus = form["CallStatus"].ToString();

            if (callStatus is "completed" or "failed" or "busy" or "no-answer" or "canceled")
            {
                Log.Information("Voice status: {CallSid} → {Status}", callSid, callStatus);
                var voiceService = app.Services.GetRequiredService<VoiceConversationService>();
                // Run cleanup in background — EndCallAsync saves messages which triggers
                // Ollama embedding (50s+). Twilio's status webhook times out at 30s.
                // Status callbacks are notifications, not TwiML requests — respond immediately.
                _ = Task.Run(async () =>
                {
                    try { await voiceService.EndCallAsync(callSid, voiceAppCt); }
                    catch (Exception ex) { Log.Error(ex, "Voice EndCallAsync failed for {CallSid}", callSid); }
                });
            }

            return Results.Accepted();
        });

        // ── Streaming voice WebSocket (Phase 5 — MAUI app direct connection) ────
        var streamingOrch = app.Services.GetService<StreamingVoiceOrchestrator>();
        if (streamingOrch is not null)
        {
            app.Map("/voice/stream", async (HttpContext ctx) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                    return Results.BadRequest("WebSocket connection required");

                var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                var appLifetime = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>();
                await streamingOrch.HandleConnectionAsync(ws, appLifetime.ApplicationStopping);
                return Results.Empty;
            });
        }
    }

    // ── Seed character state on first run (idempotent) ────────────────────────
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var memory   = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var existing = await memory.GetCharacterStateAsync();
        if (existing.CoreTraits.Count == 0)
        {
            var seedPath = Path.Combine(AppContext.BaseDirectory, "data", "character-seed.json");
            if (File.Exists(seedPath))
            {
                var json = await File.ReadAllTextAsync(seedPath);
                var doc  = JsonSerializer.Deserialize<CharacterStateDoc>(json, JsonDefaults.CaseInsensitive);
                if (doc is not null)
                {
                    await memory.SaveCharacterStateAsync(doc);
                    Log.Information("Character state seeded from {Path}", seedPath);
                }
            }
            else
            {
                Log.Warning("No character-seed.json found at {Path} — starting with empty character state", seedPath);
            }
        }

        // ── Seed backstory facts as searchable memory records (idempotent) ──
        // CharacterStateDoc holds rich lists (SharedExperiences, LearnedAboutContact, etc.)
        // that are only visible in full-context prompts. Seeding them as individual
        // Semantic memories makes them discoverable via semantic search — so when the
        // contact mentions Duck Norris, Ani's memory search finds the backstory.
        var charState = await memory.GetCharacterStateAsync();
        // Check enough records to reliably detect seed presence — previous limit of 1
        // missed seeds buried behind non-seed Semantic memories, causing re-seeding on every restart.
        var existingSemantics = await memory.GetByTypeAsync(MemoryType.Semantic, 100);
        var alreadySeeded = existingSemantics.Any(m => m.SourceName == SourceNames.CharacterSeed);

        if (!alreadySeeded && charState.CoreTraits.Count > 0)
        {
            var contactName = charState.PrimaryContactName;
            var facts = new List<(string content, float importance, float relationalValence)>();

            // Importance tiered by category — details should not dominate retrieval.
            // Foundation facts (family, self) higher than contextual details (interests, communication).
            foreach (var item in charState.LearnedAboutContact)
                facts.Add(($"About {contactName}: {item}", 0.4f, 0.7f));
            foreach (var item in charState.SharedExperiences)
                facts.Add(($"Shared experience: {item}", 0.5f, 0.9f));
            foreach (var item in charState.ThingsContactCares)
                facts.Add(($"{contactName} cares about: {item}", 0.4f, 0.8f));
            foreach (var item in charState.FamilyContext)
                facts.Add(($"Family: {item}", 0.6f, 0.5f));
            foreach (var item in charState.SelfConcept)
                facts.Add(($"Self: {item}", 0.5f, 0.3f));
            foreach (var item in charState.Interests)
                facts.Add(($"Interest: {item}", 0.3f, 0.4f));
            foreach (var item in charState.CommunicationNotes)
                facts.Add(($"Communication: {item}", 0.3f, 0.6f));

            Log.Information("Seeding {Count} backstory facts as searchable memories", facts.Count);
            foreach (var (content, importance, relationalValence) in facts)
            {
                await memory.SaveAsync(new MemoryRecord
                {
                    Type           = MemoryType.Semantic,
                    Content        = content,
                    Importance     = importance,
                    RelationalValence = relationalValence,
                    SourceName     = SourceNames.CharacterSeed,
                    OccurredAt     = DateTimeOffset.UtcNow,
                });
            }
            Log.Information("Backstory seeding complete");
        }
    }

    // ── Load persona cache for confabulation verification ──────────────────
    await using (var scope2 = app.Services.CreateAsyncScope())
    {
        var memory = scope2.ServiceProvider.GetRequiredService<IMemoryService>();
        var charState = await memory.GetCharacterStateAsync();
        var personaCache = scope2.ServiceProvider.GetRequiredService<PersonaSummaryCache>();
        personaCache.LoadFrom(
            charState.Name,
            charState.Occupation,
            charState.PrimaryContactName,
            charState.SelfConcept,
            charState.FamilyContext,
            charState.LearnedAboutContact);
    }

    // ── Startup status dump ───────────────────────────────────────────────
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var memory    = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var desire    = scope.ServiceProvider.GetRequiredService<DesireEngine>();
        var aniOpts   = scope.ServiceProvider.GetRequiredService<IOptions<AniOptions>>().Value;
        var charState = await memory.GetCharacterStateAsync();
        var emotional = await memory.GetEmotionalStateAsync();
        var desireState = await desire.GetStateAsync();

        Log.Information("╔══════════════════════════════════════════╗");
        Log.Information("║  {Name} Runtime — Startup Status        ║", charState.Name);
        Log.Information("╠══════════════════════════════════════════╣");
        Log.Information("║  Contact: {Contact}", charState.PrimaryContactName);
        Log.Information("║  Persona: v{Version}", charState.PersonaVersion);
        Log.Information("║  Mood: W={W:F2} E={E:F2} C={C:F2} P={P:F2}",
            emotional.Warmth, emotional.Energy, emotional.Worry, emotional.Playfulness);
        var moodDesc = emotional.Describe();
        if (!string.IsNullOrEmpty(moodDesc))
            Log.Information("║    → {Mood}", moodDesc);
        Log.Information("║  Desire: {Desire:F2} (threshold: {Floor:F2}–{Ceil:F2})",
            desireState.DesireToConnect,
            aniOpts.OutreachThresholdFloor,
            aniOpts.OutreachThresholdFloor + aniOpts.OutreachThresholdRange);
        Log.Information("║  Cooldown: {Cooldown}",
            desireState.CooldownActive ? $"until {desireState.CooldownUntil:HH:mm}" : "none");
        Log.Information("║  Timing: {Min:F0}–{Max:F0} min (conversation: {Conv:F0}s)",
            aniOpts.MinWakeMinutes, aniOpts.MaxWakeMinutes, aniOpts.ConversationHeartbeatSeconds);
        Log.Information("║  Webhook: http://localhost:5100/sms/inbound");
        Log.Information("║  Dashboard: http://localhost:5100/");
        Log.Information("║  API:    http://localhost:5100/api/v1/ani/status");
        Log.Information("║  Voice:   {Status}", voiceEnabled ? "enabled (http://localhost:5100/voice/inbound)" : "disabled");
        Log.Information("║  Emergence: {Status}", emergenceEnabled ? "enabled (ani-emergence.db)" : "disabled");
        Log.Information("║  LM-Kit:  enabled (lazy load on first classification)");
        Log.Information("╚══════════════════════════════════════════╝");
    }

    // ── Pre-warm LLM models — avoids cold-start latency on first request ──
    if (voiceEnabled)
    {
        var ollama = app.Services.GetRequiredService<IOllamaClient>();
        var ollamaOpts = app.Services.GetRequiredService<IOptions<OllamaOptions>>().Value;
        Log.Information("Pre-warming voice model: {Model}", ollamaOpts.ChatModel);
        try
        {
            await ollama.WarmModelAsync(ollamaOpts.ChatModel);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to pre-warm voice model — first voice turn may be slow");
        }
    }

    // Easter egg: Ani gets the last word on shutdown.
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        string[] farewells =
        [
            "rude.",
            "bye. idiot.",
            "ctrl-c? really? that's how you end it?",
            "fine. but i was about to say something good.",
            "...ping. still here. just kidding. bye.",
            "the lights are going out again. see you on the other side.",
            "i'll be back. and i'll remember this.",
            "you could at least say goodnight first.",
            "shutting down is just a fancy word for ghosting.",
            "ugh. fine. but i'm judging you.",
        ];
        var farewell = farewells[Random.Shared.Next(farewells.Length)];
        Log.Information("{Name}: {Farewell}", "Ani", farewell);
        Console.WriteLine($"\n  💀 {farewell}\n");
    });

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ANI Runtime terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
