using System.Text.Json;
using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using AniRuntime.Memory;
using AniRuntime.Perception;
using Microsoft.Extensions.Options;
using Serilog;

// ── Logging ───────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/ani-.log", rollingInterval: RollingInterval.Day)
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

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

    // ── Cognitive cycle ───────────────────────────────────────────────────────
    builder.Services.AddSingleton<AdminCommandHandler>();
    builder.Services.AddSingleton<CognitiveCycleProcessor>();
    builder.Services.AddHostedService<AniHeartbeatService>();

    var host = builder.Build();

    // ── Wire early wake: Twilio inbound → heartbeat interrupt ───────────────
    var twilioSource = host.Services.GetRequiredService<TwilioInboundPerceptionSource>();
    var heartbeat    = host.Services.GetServices<IHostedService>()
                           .OfType<AniHeartbeatService>().First();
    twilioSource.OnMessageReceived = heartbeat.RequestEarlyWake;

    // ── Seed character state on first run (idempotent) ────────────────────────
    await using (var scope = host.Services.CreateAsyncScope())
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
            var facts = new List<(string content, float importance, float contactValence)>();

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
            foreach (var (content, importance, contactValence) in facts)
            {
                await memory.SaveAsync(new MemoryRecord
                {
                    Type           = MemoryType.Semantic,
                    Content        = content,
                    Importance     = importance,
                    ContactValence = contactValence,
                    SourceName     = "character-seed",
                    OccurredAt     = DateTimeOffset.UtcNow,
                });
            }
            Log.Information("Backstory seeding complete");
        }
    }

    // ── Startup status dump ───────────────────────────────────────────────
    await using (var scope = host.Services.CreateAsyncScope())
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
            emotional.Warmth, emotional.Energy, emotional.Concern, emotional.Playfulness);
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
        Log.Information("╚══════════════════════════════════════════╝");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ANI Runtime terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
