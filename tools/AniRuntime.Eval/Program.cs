using System.Text.Json;
using AniRuntime.Core.Interfaces;
using AniRuntime.Loops;
using AniRuntime.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ════════════════════════════════════════════════════════════════════════════
// AniRuntime.Eval — Issue #79 Phase A
// CLI driver that bootstraps the runtime in-process and drives the production
// reply pipeline against an isolated eval-DB snapshot. Output is structured
// JSON suitable for harness consumption.
//
// **Memory isolation invariant**: this driver MUST NOT open the production
// memory DB in write mode. Callers pass a path to a snapshot/eval DB via
// the --db-path argument, the Ani__MemoryDbPath environment variable, or
// (in tests) an explicit JSON field. The DI registration honors the same
// Ani:MemoryDbPath config key as production, so overriding the configuration
// before AddAniRuntimeCore is sufficient.
// ════════════════════════════════════════════════════════════════════════════

var dbPath = ParseArg(args, "--db-path") ?? Environment.GetEnvironmentVariable("Ani__MemoryDbPath");
if (string.IsNullOrWhiteSpace(dbPath))
{
    Console.Error.WriteLine(
        "AniRuntime.Eval: --db-path or Ani__MemoryDbPath env var is required. " +
        "Pass an isolated eval-DB snapshot path — production memory must not be touched.");
    return 2;
}

// Build a non-Web host so background services don't auto-start.
// The runtime's hosted services (AniHeartbeatService, DiagnosticScheduler)
// are registered by the container but only activate under an IHost that
// calls StartAsync — a plain ServiceProvider doesn't.
var configBuilder = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Ani:MemoryDbPath"] = dbPath,
        // Voice / Streaming / Dashboard / Emergence are all gated by config
        // flags in the container; default them off for eval to keep the
        // bootstrap fast and the surface narrow.
        ["Voice:Enabled"] = "false",
        ["Voice:StreamingEnabled"] = "false",
        ["Images:Enabled"] = "false",
        // Emergence must be enabled: RebuildEmergenceCommand is unconditionally
        // registered as an IAdminCommand and depends on EmergenceStore. Eval
        // sessions are short-lived so the observation overhead is negligible.
        ["Emergence:Enabled"] = "true",
    });

var configuration = configBuilder.Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
AniRuntimeServiceContainer.AddAniRuntimeCore(services, configuration);

await using var provider = services.BuildServiceProvider();

// Smoke-test: resolve the key services we'll need for Phase A pipeline
// invocation. If any of these fail, the bootstrap is broken before we even
// touch a scenario.
var smokeReport = new SmokeReport
{
    DbPath = dbPath,
    Resolved = new Dictionary<string, string>(),
};

foreach (var (label, svcType) in new (string, Type)[]
{
    ("IMemoryService", typeof(IMemoryService)),
    ("IMemoryPersistence", typeof(IMemoryPersistence)),
    ("IMemorySearch", typeof(IMemorySearch)),
    ("IMemoryMergePolicy", typeof(IMemoryMergePolicy)),
    ("IConversationService", typeof(IConversationService)),
    ("ConversationReplyPhase", typeof(ConversationReplyPhase)),
    ("CognitiveCycleProcessor", typeof(CognitiveCycleProcessor)),
    ("IOllamaClient", typeof(IOllamaClient)),
    ("IReplyChannelResolver", typeof(IReplyChannelResolver)),
})
{
    try
    {
        var resolved = provider.GetService(svcType);
        smokeReport.Resolved[label] = resolved is null
            ? "NOT_REGISTERED"
            : resolved.GetType().Name;
    }
    catch (Exception ex)
    {
        smokeReport.Resolved[label] = $"RESOLVE_FAILED: {ex.GetType().Name}: {ex.Message}";
    }
}

var json = JsonSerializer.Serialize(smokeReport, new JsonSerializerOptions
{
    WriteIndented = true,
});

Console.WriteLine(json);
return 0;

// ── helpers ─────────────────────────────────────────────────────────────────

static string? ParseArg(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == name && i + 1 < args.Length)
        {
            return args[i + 1];
        }

        if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
        {
            return args[i][(name.Length + 1)..];
        }
    }

    return null;
}

// ── output shape ────────────────────────────────────────────────────────────

internal sealed record SmokeReport
{
    public required string DbPath { get; init; }
    public required Dictionary<string, string> Resolved { get; init; }
}
