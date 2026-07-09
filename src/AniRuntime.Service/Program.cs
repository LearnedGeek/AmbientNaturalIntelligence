using System.Text.Json;
using AniRuntime.Core.Utilities;
using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using AniRuntime.Loops.Coreference;
using AniRuntime.Loops.Invariants;
using AniRuntime.Loops.Pipeline;
using AniRuntime.Memory;
using AniRuntime.Perception;
using AniRuntime.Dashboard;
using AniRuntime.Emergence;
using AniRuntime.Emergence.Models;
using AniRuntime.Voice;
using LearnedGeek.ML;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;
using Twilio.Security;

// ── Log directory (absolute path, not cwd-relative) ──────────────────────────
// Windows SCM launches services with cwd = C:\Windows\System32. Using
// relative paths in Serilog meant logs were landing in System32\logs\
// instead of next to the executable. The SetCurrentDirectory approach
// (earlier attempt) didn't take effect for reasons we didn't fully
// diagnose (either WindowsServiceHelpers.IsWindowsService() returned
// false in this hosting context, or Serilog resolved paths at config
// time before cwd was changed). Using AppContext.BaseDirectory directly
// bypasses both concerns -- logs always land next to the EXE.
//
// For interactive dev (dotnet run), AppContext.BaseDirectory is
// bin/Debug/net8.0 rather than src/AniRuntime.Service -- a slight
// change in log location but not a breakage.
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");

// ── Logging ───────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDir, "ani-.log"), rollingInterval: RollingInterval.Day)
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
        .WriteTo.File(Path.Combine(logDir, "ani-.log"),
            rollingInterval: RollingInterval.Day,
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
            outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}",
            retainedFileCountLimit: 30)
        // Diagnostic — everything, for debugging.
        // 2026-05-18: sink minimum lowered from Debug to Verbose so the
        // OllamaClient full-prompt Trace-level dumps (Verbose in Serilog
        // terms) reach disk when a per-category Override allows them.
        // Pipeline-level filtering still happens via Serilog's
        // MinimumLevel.Override in appsettings.json — Verbose events only
        // fire when a category is explicitly opted in.
        .WriteTo.File(Path.Combine(logDir, "ani-debug-.log"),
            rollingInterval: RollingInterval.Day,
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Verbose,
            outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
            retainedFileCountLimit: 7));

    // ── Service registration ──────────────────────────────────────────────────
    // Composition root extracted on 2026-06-02 (Issue #79 prep) so the sibling
    // tools/AniRuntime.Eval/ host can reuse the same DI graph. See
    // AniRuntimeServiceContainer.cs for the full registration list.
    AniRuntime.Service.AniRuntimeServiceContainer.AddAniRuntimeCore(builder.Services, builder.Configuration);

    // ── Feature-flag readbacks for downstream endpoint mapping ────────────────
    // These flags gate webhook endpoint registration further down. The DI
    // registrations themselves are conditional inside AddAniRuntimeCore;
    // these reads are just for endpoint-mapping decisions on the app instance.
    var voiceEnabled = builder.Configuration.GetValue<bool>("Voice:Enabled");
    var emergenceEnabled = builder.Configuration.GetValue<bool>("Emergence:Enabled");

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

    // ── Phase 6 v1.2 retroactive compression admin endpoint (May 17, 2026) ─
    // Drives the same RunReflectionAsync machinery used by the periodic
    // reflection cycle, but on-demand. Used to compress accumulated substrate
    // generated under the pre-v1.2 architecture. Loops are driven externally
    // (by a PowerShell driver script with throttling), so this endpoint runs
    // ONE batch per call and returns metrics — that lets the driver decide
    // pacing and total iterations.
    //
    // Returns { savedThisBatch: bool, totalReflections: int } so the driver
    // can detect "no more eligible records" (savedThisBatch=false) and stop.
    app.MapPost("/api/v1/admin/retroactive-compression-step", async (
        ReflectionPhase reflection,
        IMemoryService memory,
        ILogger<Program> log,
        HttpContext ctx) =>
    {
        try
        {
            var charState = await memory.GetCharacterStateAsync(ctx.RequestAborted)
                .ConfigureAwait(false);
            var savedThisBatch = await reflection
                .ForceRunOnceAsync(charState, ctx.RequestAborted)
                .ConfigureAwait(false);

            // Total reflection-source record count for progress visibility.
            var memSearch = (IMemorySearch)memory;
            var totalReflections = (await memSearch
                .GetByTypeAsync(MemoryType.Semantic, 5000, ctx.RequestAborted)
                .ConfigureAwait(false))
                .Count(m => m.SourceName == "reflection");

            return Results.Ok(new
            {
                savedThisBatch,
                totalReflections,
                timestamp = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Retroactive compression step failed");
            return Results.Json(new
            {
                error = ex.Message,
                timestamp = DateTimeOffset.UtcNow,
            }, statusCode: 500);
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

    // ── Issue #93 Phase 4 (2026-07-09) — FTS5 hybrid retrieval index ──────────
    // Backgrounded (same rationale as WarmModelAsync below): the FTS5
    // backfill on a populated production DB (~25k rows) takes 15-40s
    // depending on I/O — inline it would push startup past Windows SCM's
    // ~30s "service did not respond" deadline (error 1053). The composer's
    // FetchBm25RanksAsync fails-open when FTS5 doesn't exist yet, so
    // retrieval degrades cleanly to composite-only during the backfill
    // window. Log lines will show bm25_rescues=0 until it completes; then
    // hybrid takes effect on the next cycle.
    _ = Task.Run(async () =>
    {
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            var ctxFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AniDbContext>>();
            await using var ctx = await ctxFactory.CreateDbContextAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ctx.EnsureFtsIndexAsync();
            sw.Stop();
            Log.Information("FTS5 hybrid-retrieval index ready ({Elapsed} ms)", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FTS5 hybrid-retrieval index init failed — falling back to composite-only");
        }
    });

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
                    // Epistemic Grounding (Apr 10): Character seeds are the
                    // highest-trust factual substrate about Mark and Ani's world.
                    // Always Facts tier — this is where Mark-about-himself and
                    // foundational relationship facts live.
                    Provenance     = EpistemicTier.Facts,
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
    //
    // Backgrounded (May 18 2026): awaiting WarmModelAsync inline could push
    // startup past Windows SCM's ~30s "service did not respond" deadline
    // (error 1053) when Ollama itself is cold — observed on ani-server
    // deploys where the cold Ollama + ~10GB model load reliably exceeded
    // 30s. The warm is best-effort anyway; the first voice request will
    // wait if the warm isn't done yet.
    if (voiceEnabled)
    {
        var ollama = app.Services.GetRequiredService<IOllamaClient>();
        var ollamaOpts = app.Services.GetRequiredService<IOptions<OllamaOptions>>().Value;
        Log.Information("Pre-warming voice model in background: {Model}", ollamaOpts.ChatModel);
        _ = Task.Run(async () =>
        {
            try
            {
                await ollama.WarmModelAsync(ollamaOpts.ChatModel);
                Log.Information("Voice model pre-warm complete: {Model}", ollamaOpts.ChatModel);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to pre-warm voice model — first voice turn may be slow");
            }
        });
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
