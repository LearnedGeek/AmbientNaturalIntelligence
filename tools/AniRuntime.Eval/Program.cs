using System.Text.Json;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Eval;
using AniRuntime.Loops;
using AniRuntime.Memory;
using AniRuntime.Perception;
using AniRuntime.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

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

// K.5-probe (2026-07-06) — retrieval-only mode. When --probe-query is
// provided, run the same IMemorySearch.SearchWithScoresAsync production
// uses and dump top-K ScoredMemory results as JSON. Skip full cycle
// invocation. Used to empirically verify what semantic search surfaces
// before wiring anything to the composer.
var probeQuery = ParseArg(args, "--probe-query");
var probeTopKRaw = ParseArg(args, "--probe-top-k") ?? "10";
var probeTopK   = int.TryParse(probeTopKRaw, out var pk) ? pk : 10;

// Issue #93 Phase 2 (2026-07-06) — classifier harness mode. When
// --classify-tag <fixture-path> is provided, load the fixture (array of
// { name, tag_note, ani_reply, mark_prior, expected_intent }), call the
// production ITagIntentClassifier for each entry, and dump per-entry
// verdicts + a summary accuracy table. Skips DB / cycle / dispatch —
// this is a pure classifier probe against a running Ollama.
var classifyTagFixture = ParseArg(args, "--classify-tag");

// Issue #93 Phase 3 (2026-07-06) — self-audit classifier harness. When
// --classify-contradiction <fixture-path> is provided, load the fixture
// (array of { name, interior_content, confirmed_substrate,
// expected_outcome }), call the production IContentContradictionClassifier
// for each entry, and dump per-entry verdicts + a summary accuracy table.
// Same shape as --classify-tag.
var classifyContradictionFixture = ParseArg(args, "--classify-contradiction");

// Issue #96 (2026-07-15) — Tool-call selection harness. When --tool-call
// <fixture-path> is provided, load the fixture (array of { name,
// user_message, conversation_context, available_tools,
// expected_should_call_tool, expected_tool_name, expected_arguments }),
// call the production IToolCallClassifier for each entry, and dump per-
// entry verdicts + a summary accuracy table. Symmetric to --classify-tag
// and --classify-contradiction: pure classifier probe, skips DB / cycle
// / dispatch. Empirical baseline for tool-selection accuracy BEFORE
// ConversationReplyPipeline integration per #96 test-first discipline.
var toolCallFixture = ParseArg(args, "--tool-call");

// Issue #93 Phase 3 (2026-07-07) — batch-size for the contradiction
// harness. 1 = single-item mode (baseline accuracy per Phase 3 first
// draft). >1 = batched mode via ClassifyBatchAsync (throughput knob for
// the retroactive sweep). Default 1 to preserve baseline behavior when
// unspecified.
var batchSizeRaw = ParseArg(args, "--batch-size") ?? "1";
var batchSize = int.TryParse(batchSizeRaw, out var bs) && bs > 0 ? bs : 1;

// Issue #93 Phase 3 (2026-07-07) — retroactive sweep mode. Runs the
// contradiction classifier over all Interior records with Validity='valid',
// invalidating those the LLM flags as contradicting confirmed substrate.
// --audit-interior             = commit mutations (writes to the DB)
// --audit-interior-dry-run     = count matches, log verdicts, no UPDATE
// --audit-limit N              = stop after N records (for sampling)
// --audit-topk-facts N         = neighbors from Facts tier (default 5)
// --audit-topk-episodic N      = neighbors from Episodic tier (default 3)
// --audit-min-confidence F     = confidence floor for invalidation
//                                (default 0.60, matches TagCommand)
var auditInterior       = args.Contains("--audit-interior");
var auditInteriorDryRun = args.Contains("--audit-interior-dry-run");
var auditLimitRaw       = ParseArg(args, "--audit-limit") ?? "0";
var auditLimit          = int.TryParse(auditLimitRaw, out var al) && al > 0 ? al : 0;
var auditTopKFacts      = int.TryParse(ParseArg(args, "--audit-topk-facts")    ?? "5", out var af) ? af : 5;
var auditTopKEpisodic   = int.TryParse(ParseArg(args, "--audit-topk-episodic") ?? "3", out var ae) ? ae : 3;
var auditMinConfidence  = float.TryParse(ParseArg(args, "--audit-min-confidence") ?? "0.60", out var am) ? am : 0.60f;
// "oldest" (default) iterates in OrderBy(OccurredAt); "newest" flips to
// OrderByDescending. Used to sample different time-bands of Interior
// records — e.g. checking whether the Apr 21 fabrication class is
// concentrated near the top of the DB rather than uniformly distributed.
var auditOrder          = (ParseArg(args, "--audit-order") ?? "oldest").ToLowerInvariant();

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
        // 2026-06-03 — flipped on to test the May 18 "structural role-flip"
        // hypothesis empirically (per ConversationReplyPipeline.cs:328 and
        // AniOptions.cs:445 comments, this branch's primary failure mode
        // — slice-leak / pipeline-parrot cascade — is what we just measured
        // at 42% self-echo + 29% frontier-verifier short-circuit rates).
        // When true, the directive block (slice + [FACTS] + CRITICAL +
        // imperative) folds into the system message instead of being
        // concatenated onto the user-role current turn. Compared against
        // the 20260603-070619-full sweep as the directive-in-user baseline.
        ["Ani:LeanConversationPromptDirectiveInSystem"] = "true",
        // 2026-06-06 — production-parity fix surfaced by the tag-coverage
        // sweep. Production appsettings.json sets ConsciousSubstrateGistEnabled
        // = true (line 66), but the eval CLI was silently inheriting the
        // AniOptions default (false), so every prior eval sweep — including
        // PR #82's A/B that measured self-echo 42% → 8.5% — ran without
        // the gist composer firing at all. The dashboard-leak class
        // documented at 6/5 21:14 production never had a chance to surface
        // in eval because the producer wasn't running. Mark 2026-06-06 22:06:
        // "i find it improbable that exactly those numbers are what is
        // causing the issue. it seems to be more related to prompt structure
        // on _ollama.ChatAsync. I assume the test harness is providing the
        // same values?" Answer was: no, the test harness was not. Now it is.
        ["Ani:ConsciousSubstrateGistEnabled"] = "true",
        ["Ani:ConsciousSubstrateGistMaxTokens"] = "200",
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
    // The global minimum has to be the more permissive of the two channels,
    // because MEL filters everything below SetMinimumLevel BEFORE per-provider
    // filters run. Earlier code set it to logLevel (default Warning), which
    // discarded the Theme O.2 Information events before they could reach
    // GateTelemetryCapture — leaving GateRuns empty in every transcript even
    // though the provider was wired. Pin global to Information; the console
    // sink still respects logLevel via its own filter so user-facing noise
    // stays at the requested level.
    b.SetMinimumLevel(LogLevel.Information);
    b.AddFilter<ConsoleLoggerProvider>(null, logLevel);
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
    // Issue #93 Phase 1 rescue (2026-07-09) — idempotent ALTER TABLE for
    // confirmed_at/confirmed_by columns + canonical backfill. Must run
    // before FTS5 init because eval DB may pre-date the 2026-07-06 SQL
    // migration.
    await ctx.EnsureIssue93SchemaAsync();
    // Issue #93 Phase 4 (2026-07-09) — FTS5 index for hybrid retrieval.
    // Idempotent; backfills once on fresh DBs, no-op afterwards.
    await ctx.EnsureFtsIndexAsync();
}

// Issue #93 Phase 2 (2026-07-06) — classifier harness branch. Runs the
// production ITagIntentClassifier against the fixture and dumps per-entry
// verdicts + a summary. Symmetric to --probe-query: exits before touching
// the cycle / dispatch path.
if (!string.IsNullOrWhiteSpace(classifyTagFixture))
{
    if (!File.Exists(classifyTagFixture))
    {
        Console.Error.WriteLine($"AniRuntime.Eval: classify-tag fixture not found at {classifyTagFixture}");
        return 2;
    }

    var fixtureJson = await File.ReadAllTextAsync(classifyTagFixture);
    var fixtureItems = JsonSerializer.Deserialize<List<TagIntentFixtureItem>>(
        fixtureJson,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    if (fixtureItems is null || fixtureItems.Count == 0)
    {
        Console.Error.WriteLine("AniRuntime.Eval: fixture parsed to empty list — check schema");
        return 2;
    }

    var classifier = provider.GetRequiredService<ITagIntentClassifier>();
    var runResults = new List<object>();
    var perExpected = new Dictionary<string, (int Correct, int Total)>(StringComparer.OrdinalIgnoreCase);

    foreach (var item in fixtureItems)
    {
        var verdict = await classifier.ClassifyAsync(
            tagNote:          item.TagNote          ?? string.Empty,
            aniReply:         item.AniReply         ?? string.Empty,
            markPriorMessage: item.MarkPrior        ?? string.Empty,
            ct:               CancellationToken.None);

        var actual = verdict.Intent.ToString().ToLowerInvariant();
        var expected = (item.ExpectedIntent ?? string.Empty).ToLowerInvariant();
        var correct = string.Equals(actual, expected, StringComparison.Ordinal);

        runResults.Add(new
        {
            name             = item.Name,
            tag_note         = item.TagNote,
            expected_intent  = expected,
            actual_intent    = actual,
            confidence       = verdict.Confidence,
            reason           = verdict.Reason,
            correct,
        });

        var bucket = perExpected.TryGetValue(expected, out var v) ? v : (Correct: 0, Total: 0);
        perExpected[expected] = (bucket.Correct + (correct ? 1 : 0), bucket.Total + 1);
    }

    var totalCorrect = perExpected.Values.Sum(v => v.Correct);
    var totalCount   = perExpected.Values.Sum(v => v.Total);

    var classifyPayload = new
    {
        fixturePath = classifyTagFixture,
        model = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AniRuntime.Core.AniOptions>>()
                .Value.LocalVerifierModelTag,
        summary = new
        {
            totalCorrect,
            totalCount,
            accuracy = totalCount > 0 ? (double)totalCorrect / totalCount : 0.0,
            perExpected = perExpected.ToDictionary(
                kv => kv.Key,
                kv => new { correct = kv.Value.Correct, total = kv.Value.Total,
                            accuracy = kv.Value.Total > 0 ? (double)kv.Value.Correct / kv.Value.Total : 0.0 }),
        },
        results = runResults,
    };

    Console.WriteLine(JsonSerializer.Serialize(classifyPayload,
        new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// Issue #93 Phase 3 (2026-07-07) — retroactive Interior audit sweep.
// Runs the contradiction classifier over Interior records with
// Validity='valid', invalidating those flagged as contradicting confirmed
// substrate above the confidence floor. Symmetric to the write-side
// invariant that will fire on new inner thoughts.
if (auditInterior || auditInteriorDryRun)
{
    var isDryRun = auditInteriorDryRun && !auditInterior;
    Console.Error.WriteLine(
        $"AUDIT_INTERIOR mode={(isDryRun ? "dry-run" : "commit")} " +
        $"order={auditOrder} " +
        $"limit={(auditLimit == 0 ? "all" : auditLimit.ToString())} " +
        $"topK_facts={auditTopKFacts} topK_episodic={auditTopKEpisodic} " +
        $"min_confidence={auditMinConfidence:F2} db={dbPath}");

    var classifier = provider.GetRequiredService<IContentContradictionClassifier>();
    var search     = provider.GetRequiredService<IMemorySearch>();
    var ctxFactory = provider.GetRequiredService<IDbContextFactory<AniDbContext>>();

    // Fetch the target set once. Interior + Validity='valid' + has embedding.
    List<AniRuntime.Memory.Entities.MemoryEntity> targets;
    await using (var loadCtx = await ctxFactory.CreateDbContextAsync())
    {
        var baseQ = loadCtx.Memories
            .Where(m => m.Provenance == EpistemicTier.Interior
                     && m.Validity   == "valid"
                     && m.Embedding  != null);
        var ordered = auditOrder == "newest"
            ? (IQueryable<AniRuntime.Memory.Entities.MemoryEntity>)baseQ.OrderByDescending(m => m.OccurredAt)
            : baseQ.OrderBy(m => m.OccurredAt);
        targets = auditLimit > 0
            ? await ordered.Take(auditLimit).ToListAsync()
            : await ordered.ToListAsync();
    }

    Console.Error.WriteLine($"AUDIT_INTERIOR loaded {targets.Count} candidate records");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var contradictsCount = 0;
    var groundedCount    = 0;
    var neutralCount     = 0;
    var unknownCount     = 0;
    var invalidatedCount = 0;
    var perOutcomeSamples = new Dictionary<string, List<object>>
    {
        ["contradicts"] = new(),
        ["grounded"]    = new(),
        ["neutral"]     = new(),
        ["unknown"]     = new(),
    };

    var processed = 0;
    foreach (var entity in targets)
    {
        processed++;
        if (processed % 50 == 0)
        {
            var pct = 100.0 * processed / targets.Count;
            var elapsed = sw.Elapsed.TotalSeconds;
            var eta = elapsed / processed * (targets.Count - processed);
            Console.Error.WriteLine(
                $"AUDIT_INTERIOR progress {processed}/{targets.Count} ({pct:F1}%) " +
                $"elapsed={elapsed:F0}s eta={eta:F0}s " +
                $"contradicts={contradictsCount} grounded={groundedCount} " +
                $"neutral={neutralCount} unknown={unknownCount} " +
                $"invalidated={invalidatedCount}");
        }

        // Retrieve top-K confirmed neighbors — Facts + Episodic — by cosine
        // against this Interior record's content. Skip retrieval if the record
        // has no content (defensive).
        if (string.IsNullOrWhiteSpace(entity.Content)) continue;

        IEnumerable<ScoredMemory> factsNeighbors;
        IEnumerable<ScoredMemory> episodicNeighbors;
        try
        {
            factsNeighbors    = await search.SearchByTierAsync(
                entity.Content, EpistemicTier.Facts,    auditTopKFacts,    CancellationToken.None);
            episodicNeighbors = await search.SearchByTierAsync(
                entity.Content, EpistemicTier.Episodic, auditTopKEpisodic, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AUDIT_INTERIOR retrieval failed id={entity.Id}: {ex.Message}");
            unknownCount++;
            continue;
        }

        var substrateLines = factsNeighbors
            .Concat(episodicNeighbors)
            .Select(s => $"- {s.Record.Content}")
            .ToList();
        var substrate = string.Join("\n", substrateLines);

        ContentContradictionVerdict verdict;
        try
        {
            verdict = await classifier.ClassifyAsync(
                interiorContent:    entity.Content,
                confirmedSubstrate: substrate,
                ct:                 CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AUDIT_INTERIOR classify failed id={entity.Id}: {ex.Message}");
            unknownCount++;
            continue;
        }

        var bucket = verdict.Outcome switch
        {
            ContradictionOutcome.Contradicts => "contradicts",
            ContradictionOutcome.Grounded    => "grounded",
            ContradictionOutcome.Neutral     => "neutral",
            _                                 => "unknown",
        };
        switch (verdict.Outcome)
        {
            case ContradictionOutcome.Contradicts: contradictsCount++; break;
            case ContradictionOutcome.Grounded:    groundedCount++;    break;
            case ContradictionOutcome.Neutral:     neutralCount++;     break;
            default:                                unknownCount++;    break;
        }

        // Keep up to 20 verbatim samples per bucket for the final report —
        // enough to eyeball what the classifier is deciding on real records
        // without ballooning output.
        if (perOutcomeSamples[bucket].Count < 20)
        {
            perOutcomeSamples[bucket].Add(new
            {
                id           = entity.Id,
                sourceName   = entity.SourceName,
                confidence   = verdict.Confidence,
                quote        = verdict.Quote,
                reason       = verdict.Reason,
                contentPreview = entity.Content.Length > 200
                                 ? entity.Content.Substring(0, 200) + "…"
                                 : entity.Content,
            });
        }

        // Commit path — only mutate when Contradicts AND confidence ≥ floor
        // AND not dry-run. Log every commit for audit reconstruction.
        if (verdict.Outcome == ContradictionOutcome.Contradicts
            && verdict.Confidence >= auditMinConfidence
            && !isDryRun)
        {
            try
            {
                await using var writeCtx = await ctxFactory.CreateDbContextAsync();
                var tracked = await writeCtx.Memories.FirstOrDefaultAsync(m => m.Id == entity.Id);
                if (tracked is not null && tracked.Validity == "valid")
                {
                    tracked.Validity = "invalid_contradiction";
                    await writeCtx.SaveChangesAsync();
                    invalidatedCount++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"AUDIT_INTERIOR update failed id={entity.Id}: {ex.Message}");
            }
        }
        else if (verdict.Outcome == ContradictionOutcome.Contradicts
                 && verdict.Confidence >= auditMinConfidence
                 && isDryRun)
        {
            invalidatedCount++;  // "would-invalidate" count in dry-run mode
        }
    }

    sw.Stop();

    var summary = new
    {
        mode           = isDryRun ? "dry-run" : "commit",
        order          = auditOrder,
        dbPath,
        limit          = auditLimit,
        topKFacts      = auditTopKFacts,
        topKEpisodic   = auditTopKEpisodic,
        minConfidence  = auditMinConfidence,
        totalProcessed = processed,
        contradicts    = contradictsCount,
        grounded       = groundedCount,
        neutral        = neutralCount,
        unknown        = unknownCount,
        invalidated    = invalidatedCount,
        elapsedSeconds = sw.Elapsed.TotalSeconds,
        secondsPerRecord = processed > 0 ? sw.Elapsed.TotalSeconds / processed : 0.0,
        projectedFullSweepHours = processed > 0
            ? (sw.Elapsed.TotalSeconds / processed) * 20626 / 3600.0
            : 0.0,
        projectedFullSweepInvalidations = processed > 0
            ? (long)((double)invalidatedCount / processed * 20626)
            : 0L,
        samplesByOutcome = perOutcomeSamples,
    };

    Console.WriteLine(JsonSerializer.Serialize(summary,
        new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// Issue #93 Phase 3 (2026-07-06) — self-audit classifier harness branch.
// Symmetric to --classify-tag but runs the IContentContradictionClassifier
// against interior/substrate pairs.
if (!string.IsNullOrWhiteSpace(classifyContradictionFixture))
{
    if (!File.Exists(classifyContradictionFixture))
    {
        Console.Error.WriteLine(
            $"AniRuntime.Eval: classify-contradiction fixture not found at {classifyContradictionFixture}");
        return 2;
    }

    var fixtureJson = await File.ReadAllTextAsync(classifyContradictionFixture);
    var fixtureItems = JsonSerializer.Deserialize<List<ContentContradictionFixtureItem>>(
        fixtureJson,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    if (fixtureItems is null || fixtureItems.Count == 0)
    {
        Console.Error.WriteLine("AniRuntime.Eval: fixture parsed to empty list — check schema");
        return 2;
    }

    var classifier = provider.GetRequiredService<IContentContradictionClassifier>();
    var runResults = new List<object>();
    var perExpected = new Dictionary<string, (int Correct, int Total)>(StringComparer.OrdinalIgnoreCase);

    // Materialize expected outcomes in fixture order so batched verdicts
    // can be zipped back to their fixture rows without recomputing.
    var expectedList = fixtureItems.Select(f => (f.ExpectedOutcome ?? string.Empty).ToLowerInvariant()).ToList();

    // Chunk into batches of `batchSize` (defaults to 1 = single-item mode).
    for (var start = 0; start < fixtureItems.Count; start += batchSize)
    {
        var chunk = fixtureItems.Skip(start).Take(batchSize).ToList();

        IReadOnlyList<ContentContradictionVerdict> verdicts;
        if (chunk.Count == 1)
        {
            var v = await classifier.ClassifyAsync(
                interiorContent:     chunk[0].InteriorContent    ?? string.Empty,
                confirmedSubstrate:  chunk[0].ConfirmedSubstrate ?? string.Empty,
                ct:                  CancellationToken.None);
            verdicts = new[] { v };
        }
        else
        {
            var pairs = chunk.Select(f => (
                f.InteriorContent    ?? string.Empty,
                f.ConfirmedSubstrate ?? string.Empty)).ToList();
            verdicts = await classifier.ClassifyBatchAsync(pairs, CancellationToken.None);
        }

        for (var k = 0; k < chunk.Count; k++)
        {
            var item     = chunk[k];
            var verdict  = verdicts[k];
            var actual   = verdict.Outcome.ToString().ToLowerInvariant();
            var expected = expectedList[start + k];
            var correct  = string.Equals(actual, expected, StringComparison.Ordinal);

            runResults.Add(new
            {
                name              = item.Name,
                expected_outcome  = expected,
                actual_outcome    = actual,
                confidence        = verdict.Confidence,
                quote             = verdict.Quote,
                reason            = verdict.Reason,
                correct,
            });

            var bucket = perExpected.TryGetValue(expected, out var v) ? v : (Correct: 0, Total: 0);
            perExpected[expected] = (bucket.Correct + (correct ? 1 : 0), bucket.Total + 1);
        }
    }

    var totalCorrect = perExpected.Values.Sum(v => v.Correct);
    var totalCount   = perExpected.Values.Sum(v => v.Total);

    var payload = new
    {
        fixturePath = classifyContradictionFixture,
        model = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AniRuntime.Core.AniOptions>>()
                .Value.LocalVerifierModelTag,
        batchSize,
        summary = new
        {
            totalCorrect,
            totalCount,
            accuracy = totalCount > 0 ? (double)totalCorrect / totalCount : 0.0,
            perExpected = perExpected.ToDictionary(
                kv => kv.Key,
                kv => new { correct = kv.Value.Correct, total = kv.Value.Total,
                            accuracy = kv.Value.Total > 0 ? (double)kv.Value.Correct / kv.Value.Total : 0.0 }),
        },
        results = runResults,
    };

    Console.WriteLine(JsonSerializer.Serialize(payload,
        new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// Issue #96 (2026-07-15) — Tool-call selection harness. Runs the production
// IToolCallClassifier against the fixture. Each fixture row supplies its own
// available_tools descriptor set so the harness can exercise different
// tool-catalogues without recompilation.
if (!string.IsNullOrWhiteSpace(toolCallFixture))
{
    if (!File.Exists(toolCallFixture))
    {
        Console.Error.WriteLine($"AniRuntime.Eval: tool-call fixture not found at {toolCallFixture}");
        return 2;
    }

    var fixtureJson = await File.ReadAllTextAsync(toolCallFixture);
    var fixtureItems = JsonSerializer.Deserialize<List<ToolCallFixtureItem>>(
        fixtureJson,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    if (fixtureItems is null || fixtureItems.Count == 0)
    {
        Console.Error.WriteLine("AniRuntime.Eval: tool-call fixture parsed to empty list — check schema");
        return 2;
    }

    var classifier = provider.GetRequiredService<IToolCallClassifier>();
    var runResults = new List<object>();
    var perExpected = new Dictionary<string, (int Correct, int Total)>(StringComparer.OrdinalIgnoreCase);

    foreach (var item in fixtureItems)
    {
        var tools = (item.AvailableTools ?? new List<ToolCallFixtureToolItem>())
            .Select(t => new ToolDescriptor(
                Name:            t.Name        ?? string.Empty,
                Description:     t.Description ?? string.Empty,
                ParameterSchema: (IReadOnlyDictionary<string, string>)(t.Parameters ?? new Dictionary<string, string>())))
            .ToList();

        var verdict = await classifier.ClassifyAsync(
            userMessage:         item.UserMessage         ?? string.Empty,
            availableTools:      tools,
            conversationContext: item.ConversationContext ?? string.Empty,
            ct:                  CancellationToken.None);

        // Bucket the verdict for the summary. We record two orthogonal
        // "correct" flags: (a) should-call decision correct, (b) if a tool
        // was called, name matches. Argument-shape check is optional per
        // fixture — if expected_arguments is present, require exact match.
        var expectedShouldCall = item.ExpectedShouldCallTool ?? false;
        var shouldCallCorrect  = verdict.ShouldCallTool == expectedShouldCall;

        var expectedToolName = (item.ExpectedToolName ?? string.Empty).Trim();
        var actualToolName   = verdict.ToolName ?? string.Empty;
        var toolNameCorrect  = expectedShouldCall
            ? string.Equals(expectedToolName, actualToolName, StringComparison.Ordinal)
            : true; // no-call cases: tool-name check is n/a

        var argumentsCorrect = true;
        if (expectedShouldCall && item.ExpectedArguments is { Count: > 0 })
        {
            argumentsCorrect = verdict.Arguments is not null
                && item.ExpectedArguments.All(kv =>
                    verdict.Arguments.TryGetValue(kv.Key, out var v)
                    && string.Equals(v, kv.Value, StringComparison.Ordinal));
        }

        var overallCorrect = shouldCallCorrect && toolNameCorrect && argumentsCorrect;
        var bucketKey = expectedShouldCall
            ? $"call:{expectedToolName}"
            : "no_call";

        runResults.Add(new
        {
            name                     = item.Name,
            user_message             = item.UserMessage,
            expected_should_call     = expectedShouldCall,
            actual_should_call       = verdict.ShouldCallTool,
            expected_tool_name       = expectedShouldCall ? expectedToolName : null,
            actual_tool_name         = verdict.ToolName,
            expected_arguments       = item.ExpectedArguments,
            actual_arguments         = verdict.Arguments,
            confidence               = verdict.Confidence,
            reason                   = verdict.Reason,
            should_call_correct      = shouldCallCorrect,
            tool_name_correct        = toolNameCorrect,
            arguments_correct        = argumentsCorrect,
            correct                  = overallCorrect,
        });

        var bucket = perExpected.TryGetValue(bucketKey, out var v2) ? v2 : (Correct: 0, Total: 0);
        perExpected[bucketKey] = (bucket.Correct + (overallCorrect ? 1 : 0), bucket.Total + 1);
    }

    var totalCorrect = perExpected.Values.Sum(v => v.Correct);
    var totalCount   = perExpected.Values.Sum(v => v.Total);

    var toolCallPayload = new
    {
        fixturePath = toolCallFixture,
        model = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AniRuntime.Core.AniOptions>>()
                .Value.LocalVerifierModelTag,
        summary = new
        {
            totalCorrect,
            totalCount,
            accuracy = totalCount > 0 ? (double)totalCorrect / totalCount : 0.0,
            perExpected = perExpected.ToDictionary(
                kv => kv.Key,
                kv => new { correct = kv.Value.Correct, total = kv.Value.Total,
                            accuracy = kv.Value.Total > 0 ? (double)kv.Value.Correct / kv.Value.Total : 0.0 }),
        },
        results = runResults,
    };

    Console.WriteLine(JsonSerializer.Serialize(toolCallPayload,
        new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// K.5-probe (2026-07-06) — retrieval-only branch. Calls IMemorySearch
// directly and dumps the top-K ScoredMemory list as JSON so we can see
// what the runtime WOULD surface for a given query without touching the
// composer, cycle, or dispatch.
if (!string.IsNullOrWhiteSpace(probeQuery))
{
    var memSearch = provider.GetRequiredService<IMemorySearch>();
    var results = await memSearch.SearchWithScoresAsync(probeQuery, probeTopK, CancellationToken.None);
    var payload = new
    {
        query = probeQuery,
        topK  = probeTopK,
        dbPath,
        results = results.Select(r => new
        {
            id             = r.Record.Id,
            provenance     = r.Record.Provenance.ToString(),
            decayTier      = r.Record.DecayTier.ToString(),
            sourceName     = r.Record.SourceName,
            importance     = r.Record.Importance,
            createdAt      = r.Record.CreatedAt,
            compositeScore = r.CompositeScore,
            cosineSim      = r.CosineSimilarity,
            originTier     = r.OriginTier.ToString(),
            content        = r.Record.Content.Length > 300
                             ? r.Record.Content.Substring(0, 300) + "…"
                             : r.Record.Content,
        }).ToList(),
    };
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
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

/// <summary>
/// Issue #93 Phase 2 — fixture-item DTO for the --classify-tag harness.
/// Field names use snake_case to match the JSON fixture at
/// <c>tools/scenarios/tag-intent-classification.json</c>.
/// </summary>
internal sealed class TagIntentFixtureItem
{
    public string? Name           { get; set; }
    public string? TagNote        { get; set; }
    public string? AniReply       { get; set; }
    public string? MarkPrior      { get; set; }
    public string? ExpectedIntent { get; set; }
    public string? Notes          { get; set; }
}

/// <summary>
/// Issue #93 Phase 3 — fixture-item DTO for the --classify-contradiction
/// harness. Field names use snake_case to match the JSON fixture at
/// <c>tools/scenarios/content-contradiction.json</c>.
/// </summary>
internal sealed class ContentContradictionFixtureItem
{
    public string? Name                { get; set; }
    public string? InteriorContent     { get; set; }
    public string? ConfirmedSubstrate  { get; set; }
    public string? ExpectedOutcome     { get; set; }
    public string? Notes               { get; set; }
}

/// <summary>
/// Issue #96 — fixture-item DTO for the --tool-call harness. Field names
/// use snake_case to match the JSON fixture at
/// <c>tools/scenarios/tool-call-selection.json</c>. Each fixture row
/// carries its own tool catalogue so different scenarios can exercise
/// different available tools.
/// </summary>
internal sealed class ToolCallFixtureItem
{
    public string?                        Name                    { get; set; }
    public string?                        UserMessage             { get; set; }
    public string?                        ConversationContext     { get; set; }
    public List<ToolCallFixtureToolItem>? AvailableTools          { get; set; }
    public bool?                          ExpectedShouldCallTool  { get; set; }
    public string?                        ExpectedToolName        { get; set; }
    public Dictionary<string, string>?    ExpectedArguments       { get; set; }
    public string?                        Notes                   { get; set; }
}

/// <summary>
/// Issue #96 — nested descriptor DTO inside <see cref="ToolCallFixtureItem"/>.
/// Maps to <see cref="AniRuntime.Core.Models.ToolDescriptor"/> at load time.
/// </summary>
internal sealed class ToolCallFixtureToolItem
{
    public string?                     Name        { get; set; }
    public string?                     Description { get; set; }
    public Dictionary<string, string>? Parameters  { get; set; }
}
