using System.Text.Json;
using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using AniRuntime.Memory;
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
        .WriteTo.File("logs/ani-.log", rollingInterval: RollingInterval.Day));

    var config = builder.Configuration;

    // ── Options (deferred reads — all resolved after Build()) ─────────────────
    builder.Services.Configure<AniOptions>(config.GetSection("Ani"));
    builder.Services.Configure<OllamaOptions>(config.GetSection("Ollama"));
    builder.Services.Configure<TwilioOptions>(config.GetSection("Twilio"));

    // ── Core services ─────────────────────────────────────────────────────────
    builder.Services.AddSingleton<IMemoryService, SqliteMemoryService>();
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

    // ── Perception sources (Phase 2+, uncomment as implemented) ──────────────
    // builder.Services.AddSingleton<IPerceptionSource, HomeAssistantSource>();
    // builder.Services.AddSingleton<IPerceptionSource, BlogPerceptionSource>();
    // builder.Services.AddSingleton<IPerceptionSource, RssPerceptionSource>();
    // builder.Services.AddSingleton<IPerceptionSource, CalendarPerceptionSource>();

    // ── Cognitive cycle ───────────────────────────────────────────────────────
    builder.Services.AddSingleton<CognitiveCycleProcessor>();
    builder.Services.AddHostedService<AniHeartbeatService>();

    var host = builder.Build();

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
