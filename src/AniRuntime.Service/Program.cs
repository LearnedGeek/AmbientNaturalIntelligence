using System.Text.Json;
using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using AniRuntime.Memory;
using AniRuntime.Perception;
using AniRuntime.Dashboard;
using AniRuntime.Voice;
using Microsoft.AspNetCore.HttpOverrides;
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
        .WriteTo.Console()
        // Journal — inner thoughts, outreach decisions, messages sent (queryable story)
        .WriteTo.File("logs/ani-.log",
            rollingInterval: RollingInterval.Day,
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
            outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
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
    builder.Services.Configure<VoiceOptions>(config.GetSection("Voice"));

    // ── Core services ─────────────────────────────────────────────────────────
    builder.Services.AddSingleton<IMemoryService, SqliteMemoryService>();
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
    if (voiceEnabled)
    {
        builder.Services.AddHttpClient<ITextToSpeechService, ElevenLabsTextToSpeechService>();
        builder.Services.AddHttpClient<ISpeechToTextService, WhisperSpeechToTextService>();
        builder.Services.AddSingleton<TwilioVoiceHandler>();
        builder.Services.AddSingleton<MediaCacheService>();
        builder.Services.AddSingleton<IMediaEnrichmentService, VoiceMediaEnrichmentService>();
    }

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
    builder.Services.AddHttpClient("twilio");
    builder.Services.AddSingleton<TwilioInboundPerceptionSource>();
    builder.Services.AddSingleton<IPerceptionSource>(sp => sp.GetRequiredService<TwilioInboundPerceptionSource>());
    // builder.Services.AddSingleton<IPerceptionSource, HomeAssistantSource>();
    // builder.Services.AddSingleton<IPerceptionSource, CalendarPerceptionSource>();

    // ── Dashboard (Blazor Server + REST API) ──────────────────────────────────
    builder.Services.AddDashboard();

    // ── Cognitive cycle ───────────────────────────────────────────────────────
    builder.Services.AddSingleton<AdminCommandHandler>();
    builder.Services.AddSingleton<CognitiveCycleProcessor>();
    builder.Services.AddHostedService<AniHeartbeatService>();

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
    app.UseStaticFiles();

    // ── Dashboard — REST API endpoints + Blazor Server ────────────────────
    app.MapDashboard();

    // ── Wire early wake: Twilio webhook → heartbeat interrupt ─────────────────
    var twilioSource = app.Services.GetRequiredService<TwilioInboundPerceptionSource>();
    var heartbeat    = app.Services.GetServices<IHostedService>()
                           .OfType<AniHeartbeatService>().First();
    twilioSource.OnMessageReceived = heartbeat.RequestEarlyWake;

    // ── Inbound SMS webhook ──────────────────────────────────────────────────
    // Twilio POSTs here when an SMS arrives at Ani's number.
    // Enqueues the message for the cognitive cycle and triggers an early wake.
    app.MapPost("/sms/inbound", async (HttpContext ctx, IOptions<TwilioOptions> twilioOpts) =>
    {
        var form = await ctx.Request.ReadFormAsync();

        // Validate Twilio request signature
        var authToken = twilioOpts.Value.AuthToken;
        var signature = ctx.Request.Headers["X-Twilio-Signature"].FirstOrDefault() ?? "";
        var requestUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.Path}";
        var parameters = form.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

        var validator = new RequestValidator(authToken);
        if (!string.IsNullOrWhiteSpace(authToken) && !validator.Validate(requestUrl, parameters, signature))
        {
            Log.Warning("Rejected inbound SMS webhook — invalid Twilio signature");
            return Results.StatusCode(403);
        }

        var body = form["Body"].ToString();
        var messageSid = form["MessageSid"].ToString();

        if (!string.IsNullOrWhiteSpace(body))
        {
            // Enqueue the message and trigger early wake — the cognitive cycle
            // will process it immediately via PollAsync draining the queue
            twilioSource.EnqueueInbound(messageSid, body, DateTimeOffset.UtcNow);
            Log.Information("Webhook: inbound SMS enqueued ({Sid})", messageSid);
        }

        // Empty TwiML — no auto-reply, Ani will respond via the cognitive cycle
        return Results.Content("<Response></Response>", "application/xml");
    });

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

    // ── Inbound Voice webhook (Feature 20) ─────────────────────────────────────
    // Twilio POSTs here when a voice recording is ready. Transcribes via Whisper
    // and enqueues the text into the same conversation pipeline as SMS.
    if (voiceEnabled)
    {
        app.MapPost("/voice/inbound", async (HttpContext ctx, IOptions<TwilioOptions> twilioOpts) =>
        {
            var form = await ctx.Request.ReadFormAsync();

            // Validate Twilio request signature
            var authToken = twilioOpts.Value.AuthToken;
            var signature = ctx.Request.Headers["X-Twilio-Signature"].FirstOrDefault() ?? "";
            var requestUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.Path}";
            var parameters = form.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

            var validator = new Twilio.Security.RequestValidator(authToken);
            if (!string.IsNullOrWhiteSpace(authToken) && !validator.Validate(requestUrl, parameters, signature))
            {
                Log.Warning("Rejected inbound voice webhook — invalid Twilio signature");
                return Results.StatusCode(403);
            }

            var recordingUrl = form["RecordingUrl"].ToString();
            if (string.IsNullOrWhiteSpace(recordingUrl))
            {
                // Initial call — respond with TwiML to record
                var twiml = "<Response><Say>Hey, leave me a message.</Say><Record maxLength=\"120\" action=\"/voice/inbound\" /></Response>";
                return Results.Content(twiml, "application/xml");
            }

            // Recording ready — transcribe and enqueue
            var voiceHandler = app.Services.GetRequiredService<TwilioVoiceHandler>();
            var text = await voiceHandler.TranscribeInboundAsync(recordingUrl);
            if (!string.IsNullOrWhiteSpace(text))
            {
                twilioSource.EnqueueInbound($"voice-{Guid.NewGuid():N}", text, DateTimeOffset.UtcNow);
                Log.Information("Voice webhook: transcribed and enqueued ({Chars} chars)", text.Length);
            }

            return Results.Content("<Response></Response>", "application/xml");
        });
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
                var doc  = JsonSerializer.Deserialize<CharacterStateDoc>(json,
                               new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
        var existingSemantics = await memory.GetByTypeAsync(MemoryType.Semantic, 1);
        var alreadySeeded = existingSemantics.Any(m => m.SourceName == "character-seed");

        if (!alreadySeeded && charState.CoreTraits.Count > 0)
        {
            var contactName = charState.PrimaryContactName;
            var facts = new List<(string content, float importance, float relationalValence)>();

            foreach (var item in charState.LearnedAboutContact)
                facts.Add(($"About {contactName}: {item}", 0.8f, 0.7f));
            foreach (var item in charState.SharedExperiences)
                facts.Add(($"Shared experience: {item}", 0.9f, 0.9f));
            foreach (var item in charState.ThingsContactCares)
                facts.Add(($"{contactName} cares about: {item}", 0.7f, 0.8f));
            foreach (var item in charState.FamilyContext)
                facts.Add(($"Family: {item}", 0.6f, 0.5f));
            foreach (var item in charState.SelfConcept)
                facts.Add(($"Self: {item}", 0.5f, 0.3f));
            foreach (var item in charState.Interests)
                facts.Add(($"Interest: {item}", 0.5f, 0.4f));
            foreach (var item in charState.CommunicationNotes)
                facts.Add(($"Communication: {item}", 0.7f, 0.6f));

            Log.Information("Seeding {Count} backstory facts as searchable memories", facts.Count);
            foreach (var (content, importance, relationalValence) in facts)
            {
                await memory.SaveAsync(new MemoryRecord
                {
                    Type           = MemoryType.Semantic,
                    Content        = content,
                    Importance     = importance,
                    RelationalValence = relationalValence,
                    SourceName     = "character-seed",
                    OccurredAt     = DateTimeOffset.UtcNow,
                });
            }
            Log.Information("Backstory seeding complete");
        }
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
        Log.Information("╚══════════════════════════════════════════╝");
    }

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
