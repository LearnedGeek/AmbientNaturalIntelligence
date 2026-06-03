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
        // Twilio config — fake values so TwilioInboundPerceptionSource.IsEnabled
        // returns true. Without these, PollAsync's `if (!IsEnabled) return [];`
        // short-circuits before draining the synthetic message queue. The
        // runtime resolves inbound contact from CharacterState.PrimaryContactName
        // (not from the SMS From field), so a fake AccountSid/ToNumber is fine
        // — only IsEnabled needs to be true. The Twilio REST safety-net call
        // inside PollAsync will fail with 401 but our enqueued message gets
        // drained first.
        ["Twilio:InboundEnabled"] = "true",
        ["Twilio:AccountSid"] = "AC_EVAL_FAKE_NEVER_DISPATCHES",
        ["Twilio:AuthToken"] = "eval-fake-token",
        ["Twilio:ToNumber"] = "+15555550000",
        ["Voice:Enabled"] = "false",
        ["Voice:StreamingEnabled"] = "false",
        ["Images:Enabled"] = "false",
        // Emergence must be enabled: RebuildEmergenceCommand is unconditionally
        // registered as an IAdminCommand and depends on EmergenceStore.
        ["Emergence:Enabled"] = "true",
    });

var configuration = configBuilder.Build();

var services = new ServiceCollection();
var logLevel = ParseArg(args, "--log-level") switch
{
    "trace" => LogLevel.Trace,
    "debug" => LogLevel.Debug,
    "info" => LogLevel.Information,
    "warn" or null => LogLevel.Warning,
    "error" => LogLevel.Error,
    _ => LogLevel.Warning,
};

// Phase E telemetry: capture Theme O.2 pipeline events (O_HANDLER_START/END,
// O_PIPELINE_START/END) into an in-memory list so we can emit per-stage gate
// verdicts in the JSON result without re-parsing stderr.
var telemetry = new GateTelemetryCapture();

// Route ALL logs to stderr so stdout contains only the JSON result.
// Harness consumers pipe stdout to JSON-parsing without log-noise contamination.
services.AddLogging(b =>
{
    b.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
    b.AddProvider(telemetry);
    // The telemetry capture needs Information-level events; clamp the minimum
    // so we always see O_HANDLER_END regardless of the user-facing log-level
    // (which controls the console sink only).
    b.AddFilter<GateTelemetryCapture>("AniRuntime.Loops.Pipeline.CognitivePipeline", LogLevel.Information);
    b.SetMinimumLevel(logLevel);
});
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
var ctxFactory2 = provider.GetRequiredService<IDbContextFactory<AniDbContext>>();

// Phase D telemetry: snapshot memory-table state BEFORE the cycle.
var memBefore = await SnapshotMemoryStateAsync(ctxFactory2);

var syntheticSid = $"eval-{Guid.NewGuid():N}";
inbound.EnqueueInbound(syntheticSid, userMessage, DateTimeOffset.UtcNow);

var result = new EvalResult
{
    DbPath = dbPath,
    UserMessage = userMessage,
    SyntheticSid = syntheticSid,
    MemoryStateBefore = memBefore,
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

// Phase D telemetry: snapshot AFTER + compute delta (records inserted
// during this cycle by created_at > before-baseline).
var memAfter = await SnapshotMemoryStateAsync(ctxFactory2);
result.MemoryStateAfter = memAfter;
result.MemoryDelta = await ComputeMemoryDeltaAsync(ctxFactory2, memBefore);

// Phase E telemetry: extract per-stage gate verdicts from captured Theme O.2
// events. Each artifact's verdict chain is bounded by an O_PIPELINE_START /
// O_PIPELINE_END pair; in between are zero or more O_HANDLER_END events with
// stage/handler/result/details. We group by ArtifactId so multiple
// pipeline runs in the same cycle (e.g. inner thought + reply) stay separate.
result.GateRuns = ExtractGateRuns(telemetry.Events);

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    WriteIndented = true,
}));

return result.CycleCompleted ? 0 : 1;

// ── Phase D helpers ─────────────────────────────────────────────────────────

static async Task<MemoryStateSnapshot> SnapshotMemoryStateAsync(IDbContextFactory<AniDbContext> ctxFactory)
{
    await using var ctx = await ctxFactory.CreateDbContextAsync();
    var totalMemories = await ctx.Memories.CountAsync();
    var maxMemoryCreatedAt = totalMemories == 0
        ? DateTimeOffset.MinValue
        : await ctx.Memories.MaxAsync(m => m.CreatedAt);
    var totalAudit = await ctx.MemoryAudit.CountAsync();
    var totalContradictions = await ctx.MemoryContradictions.CountAsync();
    var totalMessages = await ctx.ConversationMessages.CountAsync();
    var totalThreads = await ctx.ConversationThreads.CountAsync();
    var totalClosed = await ctx.ClosedConversationRecords.CountAsync();
    return new MemoryStateSnapshot
    {
        Memories = totalMemories,
        MaxMemoryCreatedAt = maxMemoryCreatedAt,
        MemoryAuditEntries = totalAudit,
        Contradictions = totalContradictions,
        ConversationMessages = totalMessages,
        ConversationThreads = totalThreads,
        ClosedConversationRecords = totalClosed,
    };
}

static List<GateRun> ExtractGateRuns(IReadOnlyList<GateEvent> events)
{
    // Group consecutive events by ArtifactId. The runtime emits
    // O_PIPELINE_START first (with artifact_id), then any number of
    // O_HANDLER_END events for that pipeline, then O_PIPELINE_END.
    var runs = new List<GateRun>();
    GateRun? current = null;

    lock (events)
    {
        foreach (var ev in events)
        {
            switch (ev.EventName)
            {
                case "O_PIPELINE_START":
                    if (current != null) runs.Add(current);
                    current = new GateRun
                    {
                        ArtifactId = ev.Properties.GetValueOrDefault("ArtifactId", ""),
                        Producer = ev.Properties.GetValueOrDefault("Producer", ""),
                        Mode = ev.Properties.GetValueOrDefault("Mode", "Full"),
                        StartedAt = ev.Timestamp,
                        HandlerVerdicts = new List<HandlerVerdict>(),
                    };
                    break;

                case "O_HANDLER_END":
                    if (current is not null)
                    {
                        current.HandlerVerdicts.Add(new HandlerVerdict(
                            Stage: ev.Properties.GetValueOrDefault("Stage", ""),
                            Handler: ev.Properties.GetValueOrDefault("Handler", ""),
                            Result: ev.Properties.GetValueOrDefault("Result", ""),
                            DurationMs: int.TryParse(ev.Properties.GetValueOrDefault("Duration", "0"), out var d) ? d : 0,
                            Details: ev.Properties.GetValueOrDefault("Details", "")));
                    }
                    break;

                case "O_PIPELINE_END":
                    if (current is not null)
                    {
                        current.FinalResult = ev.Properties.GetValueOrDefault("Result", "");
                        current.ShortCircuitHandler = ev.Properties.GetValueOrDefault("Handler", "");
                        current.ShortCircuitReason = ev.Properties.GetValueOrDefault("Reason", "");
                        current.EndedAt = ev.Timestamp;
                        runs.Add(current);
                        current = null;
                    }
                    break;
            }
        }
    }

    if (current is not null) runs.Add(current);
    return runs;
}

static async Task<MemoryDelta> ComputeMemoryDeltaAsync(
    IDbContextFactory<AniDbContext> ctxFactory,
    MemoryStateSnapshot before)
{
    await using var ctx = await ctxFactory.CreateDbContextAsync();

    // Records whose created_at is strictly after the pre-cycle baseline.
    // This catches both inserts (new rows) and content updates that
    // refresh created_at. For supersession (Feature 30, future), the
    // is_resolved flag flip OR a future superseded_by linkage will need
    // its own field; for v1 this is the simplest reliable signal.
    var inserted = await ctx.Memories
        .Where(m => m.CreatedAt > before.MaxMemoryCreatedAt)
        .OrderBy(m => m.CreatedAt)
        .Select(m => new MemoryRecordSummary(
            m.Id,
            m.Type.ToString(),
            m.SourceName ?? "",
            m.Content.Length > 200 ? m.Content.Substring(0, 200) + "…" : m.Content,
            m.IsResolved,
            m.CreatedAt))
        .ToListAsync();

    return new MemoryDelta
    {
        InsertedSinceBefore = inserted,
        MemoriesNet = inserted.Count,
    };
}

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
    public MemoryStateSnapshot? MemoryStateBefore { get; set; }
    public MemoryStateSnapshot? MemoryStateAfter { get; set; }
    public MemoryDelta? MemoryDelta { get; set; }
    public List<GateRun> GateRuns { get; set; } = new();
}

internal sealed class GateRun
{
    public required string ArtifactId { get; init; }
    public required string Producer { get; init; }
    public required string Mode { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public required List<HandlerVerdict> HandlerVerdicts { get; init; }
    public string FinalResult { get; set; } = "";
    public string ShortCircuitHandler { get; set; } = "";
    public string ShortCircuitReason { get; set; } = "";
}

internal sealed record HandlerVerdict(
    string Stage,
    string Handler,
    string Result,
    int DurationMs,
    string Details);

internal sealed record CapturedReplyDto(string Message, DateTimeOffset CapturedAt);

internal sealed record MemoryStateSnapshot
{
    public int Memories { get; init; }
    public DateTimeOffset MaxMemoryCreatedAt { get; init; }
    public int MemoryAuditEntries { get; init; }
    public int Contradictions { get; init; }
    public int ConversationMessages { get; init; }
    public int ConversationThreads { get; init; }
    public int ClosedConversationRecords { get; init; }
}

internal sealed record MemoryDelta
{
    public required List<MemoryRecordSummary> InsertedSinceBefore { get; init; }
    public int MemoriesNet { get; init; }
}

internal sealed record MemoryRecordSummary(
    Guid Id,
    string Type,
    string SourceName,
    string ContentPreview,
    bool IsResolved,
    DateTimeOffset CreatedAt);
