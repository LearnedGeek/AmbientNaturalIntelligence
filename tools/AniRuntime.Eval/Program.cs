using System.Text.Json;
using AniRuntime.Core.Interfaces;
using AniRuntime.Eval;
using AniRuntime.Loops;
using AniRuntime.Memory;
using AniRuntime.Perception;
using AniRuntime.Service;
using Microsoft.EntityFrameworkCore;
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
// the --db-path argument or the Ani__MemoryDbPath environment variable.
//
// Usage:
//   AniRuntime.Eval --db-path PATH                       # smoke-resolve report
//   AniRuntime.Eval --db-path PATH --message "hello"    # drive a turn, capture reply
// ════════════════════════════════════════════════════════════════════════════

var dbPath = ParseArg(args, "--db-path") ?? Environment.GetEnvironmentVariable("Ani__MemoryDbPath");
if (string.IsNullOrWhiteSpace(dbPath))
{
    Console.Error.WriteLine(
        "AniRuntime.Eval: --db-path or Ani__MemoryDbPath env var is required. " +
        "Pass an isolated eval-DB snapshot path — production memory must not be touched.");
    return 2;
}

var userMessage = ParseArg(args, "--message");

// Ollama endpoint — defaults to ani-server where the trained models live.
// Override for laptop testing via --ollama-url or Ollama__BaseUrl env var.
var ollamaUrl = ParseArg(args, "--ollama-url")
    ?? Environment.GetEnvironmentVariable("Ollama__BaseUrl")
    ?? "http://ani-server:11434";

// Build a non-Web host so background services don't auto-start.
var configBuilder = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Ani:MemoryDbPath"] = dbPath,
        ["Ollama:BaseUrl"] = ollamaUrl,
        ["Ollama:ChatModel"] = "ani-v7-conversation",
        ["Ollama:InnerMonologueModel"] = "ani-v7-inner",
        ["Ollama:EmbedModel"] = "nomic-embed-text",
        ["Ollama:SubstrateModel"] = "hf.co/RichardErkhov/lzw1008_-_Emollama-7b-gguf:Q4_K_M",
        ["Voice:Enabled"] = "false",
        ["Voice:StreamingEnabled"] = "false",
        ["Images:Enabled"] = "false",
        // Emergence must be enabled: RebuildEmergenceCommand is unconditionally
        // registered as an IAdminCommand and depends on EmergenceStore.
        ["Emergence:Enabled"] = "true",
    });

var configuration = configBuilder.Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
AniRuntimeServiceContainer.AddAniRuntimeCore(services, configuration);

// ── Override the reply channel + resolver with capturing implementations ──
// Last-registration-wins for GetService<T>; our capturing resolver shadows
// the production ReplyChannelResolver. The capturing channel is exposed as
// itself so Program.cs can read captured replies after the cycle runs.
services.AddSingleton<CapturingReplyChannel>();
services.AddSingleton<IReplyChannelResolver, CapturingReplyChannelResolver>();

await using var provider = services.BuildServiceProvider();

// Ensure the eval-DB has the EF Core schema. For a fresh / empty DB this
// creates all tables from OnModelCreating. For a populated production-
// snapshot DB this is a no-op (EnsureCreated does nothing when tables
// already exist). Schema-parity between EF Core model and production is
// validated by the Phase 1.7 schema-diff test.
{
    var ctxFactory = provider.GetRequiredService<IDbContextFactory<AniDbContext>>();
    await using var ctx = await ctxFactory.CreateDbContextAsync();
    await ctx.Database.EnsureCreatedAsync();
}

if (string.IsNullOrWhiteSpace(userMessage))
{
    var smoke = BuildSmokeReport(provider, dbPath);
    Console.WriteLine(JsonSerializer.Serialize(smoke, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// ── Pipeline invocation: enqueue synthetic message + run one cycle ────────
var inbound = provider.GetRequiredService<TwilioInboundPerceptionSource>();
var processor = provider.GetRequiredService<CognitiveCycleProcessor>();
var capturer = provider.GetRequiredService<CapturingReplyChannel>();

var syntheticSid = $"eval-{Guid.NewGuid():N}";
inbound.EnqueueInbound(syntheticSid, userMessage, DateTimeOffset.UtcNow);

var result = new EvalResult
{
    DbPath = dbPath,
    UserMessage = userMessage,
    SyntheticSid = syntheticSid,
};

try
{
    using var cycleCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await processor.RunAsync(cycleCts.Token);
    result.CycleCompleted = true;
}
catch (Exception ex)
{
    result.CycleCompleted = false;
    result.Error = $"{ex.GetType().Name}: {ex.Message}";
}

result.CapturedReplies = capturer.CapturedReplies
    .Select(r => new CapturedReplyDto(r.Message, r.CapturedAt))
    .ToList();

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    WriteIndented = true,
}));

return result.CycleCompleted ? 0 : 1;

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

static SmokeReport BuildSmokeReport(IServiceProvider provider, string dbPath)
{
    var report = new SmokeReport
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
        ("CapturingReplyChannel", typeof(CapturingReplyChannel)),
    })
    {
        try
        {
            var resolved = provider.GetService(svcType);
            report.Resolved[label] = resolved is null
                ? "NOT_REGISTERED"
                : resolved.GetType().Name;
        }
        catch (Exception ex)
        {
            report.Resolved[label] = $"RESOLVE_FAILED: {ex.GetType().Name}: {ex.Message}";
        }
    }

    return report;
}

// ── output shapes ───────────────────────────────────────────────────────────

internal sealed record SmokeReport
{
    public required string DbPath { get; init; }
    public required Dictionary<string, string> Resolved { get; init; }
}

internal sealed class EvalResult
{
    public required string DbPath { get; init; }
    public required string UserMessage { get; init; }
    public required string SyntheticSid { get; init; }
    public bool CycleCompleted { get; set; }
    public string? Error { get; set; }
    public List<CapturedReplyDto> CapturedReplies { get; set; } = new();
}

internal sealed record CapturedReplyDto(string Message, DateTimeOffset CapturedAt);
