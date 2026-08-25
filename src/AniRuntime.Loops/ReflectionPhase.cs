using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Feature 32: Periodic reflection synthesis (Park et al.-inspired).
///
/// Every N cognitive cycles, synthesizes recent memories into higher-order
/// relational observations: "Mark's been checking on me a lot this week."
/// These become high-quality retrieval targets that produce more personal
/// conversation and feed the emergence layer with relational patterns.
///
/// Extracted as a separate phase (SRP) following the PerceptionPhase and
/// InnerThoughtPhase pattern.
/// </summary>
public class ReflectionPhase
{
    private readonly IOllamaClient _ollama;
    private readonly IMemoryPersistence _persist;
    private readonly IMemorySearch _search;
    private readonly AniOptions _options;
    private readonly ICognitiveOutputGate? _outputGate;
    private readonly IReflectionGistService? _gistService;
    private readonly ILogger<ReflectionPhase> _log;
    private int _cyclesSinceLastReflection;

    public ReflectionPhase(
        IOllamaClient ollama,
        IMemoryPersistence persist,
        IMemorySearch search,
        IOptions<AniOptions> options,
        ILogger<ReflectionPhase> log,
        ICognitiveOutputGate? outputGate = null,
        IReflectionGistService? gistService = null)
    {
        _ollama = ollama;
        _persist = persist;
        _search = search;
        _options = options.Value;
        _outputGate = outputGate;
        _gistService = gistService;
        _log = log;
    }

    /// <summary>
    /// Phase 6 v1.2 R1 (2026-05-18): decay-driven trigger.
    ///
    /// When <see cref="AniOptions.CompressionEnabled"/> is on, reflection
    /// fires every cognitive cycle. <see cref="RunReflectionAsync"/>
    /// pulls decay-eligible records via
    /// <c>IMemoryPersistence.GetDecayEligibleAsync</c> and early-returns
    /// when fewer than 3 are available, so the per-cycle cost when there
    /// is nothing to compress is one cheap SQL query. This decouples
    /// reflection from the wall-clock cycle interval and ties it to the
    /// existing decay scoring — reflection happens when substrate has
    /// actually aged past the importance × recency threshold, not on a
    /// 6-hour schedule. See
    /// <c>docs/spec/design/ANI-MemoryReform-Design.md</c> v1.2 R1.
    ///
    /// When <see cref="AniOptions.CompressionEnabled"/> is off, the legacy
    /// cycle-counter path runs (rollback shape for the v1 behaviour:
    /// recent-N selection, no soft-delete, fixed ReflectionCycleInterval).
    /// </summary>
    public async Task<bool> TryRunAsync(
        CharacterStateDoc characterState, CancellationToken ct)
    {
        // F-5 Phase 2 (2026-08-24) — phase scope tag so log lines emitted
        // inside reflection synthesis + compression render as
        // [cid:.../Reflection] and are filterable by phase across cycles.
        using var phaseScope = _log.BeginScope(
            new Dictionary<string, object> { ["CyclePhase"] = "Reflection" });

        if (!_options.ReflectionEnabled) return false;

        if (!_options.CompressionEnabled)
        {
            // Legacy v1 cycle-counter trigger — preserved as flag-off rollback.
            _cyclesSinceLastReflection++;
            if (_cyclesSinceLastReflection < _options.ReflectionCycleInterval)
                return false;
            _cyclesSinceLastReflection = 0;
        }

        try
        {
            await RunReflectionAsync(characterState, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reflection synthesis failed — continuing without reflection");
            return false;
        }
    }

    /// <summary>
    /// Phase 6 v1.2 retroactive scan (May 17, 2026) — force-runs ONE
    /// compression batch immediately, bypassing the cycle-counter rate
    /// limit. Used by the retroactive-compression admin endpoint to drive
    /// many batches in sequence and clear accumulated substrate generated
    /// under the pre-v1.2 architecture (~10K records in the production
    /// snapshot, of which the oldest meet the decay-eligibility predicate).
    ///
    /// Does NOT reset the periodic <c>_cyclesSinceLastReflection</c> counter
    /// so periodic reflection continues on its own schedule. Returns true
    /// if at least one summary was saved this batch (caller uses to detect
    /// "no more eligible records — stop looping").
    /// </summary>
    public async Task<bool> ForceRunOnceAsync(
        CharacterStateDoc characterState, CancellationToken ct)
    {
        if (!_options.ReflectionEnabled)
        {
            _log.LogInformation("ForceRunOnceAsync: skipped (ReflectionEnabled=false)");
            return false;
        }

        try
        {
            // 2026-05-17 evening fix: ask RunReflectionAsync directly for
            // the saved count. The prior before/after-count heuristic was
            // unreliable once reflection records could be Compressed —
            // source flips out of the visible count masked new gists being
            // added. Direct signal is correct: "did synthesis save gists."
            var saved = await RunReflectionAsync(characterState, ct).ConfigureAwait(false);
            _log.LogInformation(
                "ForceRunOnceAsync: completed (gists_saved={Saved})", saved);
            return saved > 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "ForceRunOnceAsync: failed — continuing (next batch can retry)");
            return false;
        }
    }

    /// <summary>
    /// Runs one reflection synthesis cycle. Returns the count of gists
    /// actually saved this cycle (0 if no eligible records, no parseable
    /// synthesis, or all observations skipped). The caller uses this to
    /// determine "did this cycle produce work." Driver scripts terminate
    /// after N consecutive zero-gist returns.
    ///
    /// 2026-05-17 evening fix: the prior signal (count delta of
    /// reflection-source records before/after via GetByTypeAsync, which
    /// excludes Compressed tier) was unreliable once the relaxed predicate
    /// allowed reflection records to be Compressed — sources flipping out
    /// of the visible count masked new gists being added. Returning the
    /// actual saved count direct from the synthesis loop is the correct
    /// signal: "did this cycle do productive work."
    /// </summary>
    private async Task<int> RunReflectionAsync(
        CharacterStateDoc characterState, CancellationToken ct)
    {
        // Phase 6 v1.2 R1 (May 17, 2026): when CompressionEnabled, select
        // decay-eligible records for compression. Falls back to GetRecentAsync
        // when the flag is off (legacy/rollback path). The decay-driven
        // selection criterion + post-synthesis soft-delete (R2 below) close
        // the May 17 substrate-feedback-loop finding: routine background
        // records get compressed instead of accumulating as new substrate.
        // See docs/spec/design/ANI-MemoryReform-Design.md v1.2.
        var recentMemories = _options.CompressionEnabled
            ? (await _persist.GetDecayEligibleAsync(10, ct).ConfigureAwait(false)).ToList()
            : (await _persist.GetRecentAsync(10, ct).ConfigureAwait(false)).ToList();

        if (recentMemories.Count < 3)
        {
            _log.LogDebug(
                "Reflection skipped — only {Count} {Source} memories (need at least 3)",
                recentMemories.Count,
                _options.CompressionEnabled ? "decay-eligible" : "recent");
            return 0;
        }

        var contact = characterState.PrimaryContactName ?? "Mark";

        // F-2 Phase 2 W2 (2026-08-23): pass MemoryRecord instances directly
        // instead of projecting through .Content. Pre-W2 the caller stripped
        // AttributedTo/Provenance/SourceName before the reflection composer
        // saw the sources — the C4 "compression pipe erases attribution"
        // finding from the F-2 Phase 2 audit. The command renders each
        // source with the P4 attribution-tag shape so the composer can
        // preserve per-source authorship in its gist output.
        var (system, user) = PromptBuilder.BuildReflectionSynthesisPrompt(
            characterState.Name, contact, recentMemories);

        // Phase 6 v1.2 R3 (May 17, 2026) — JSON-mode synthesis.
        //
        // R3.0 used InnerMonologueChatJsonAsync (ani-v7-inner). The 2026-05-17
        // out-of-band probe showed ani-v7-inner producing malformed JSON
        // (multiple "summaries" keys, one per source memory) and echoing
        // verbatim source content into the "shape" field — the fine-tune's
        // training pulls toward Ani's first-person inner-thought register
        // and per-utterance output, which is exactly the wrong tool for
        // compression-style summarization.
        //
        // R3.1 (May 17, 2026, same-day refinement): switched to the local
        // utility model (Qwen 14B, configured as LocalVerifierModelTag).
        // Qwen is general-purpose and not fine-tuned for any specific voice,
        // so it respects both the JSON schema and the compression-style
        // instruction without fighting training. Same model as the frontier
        // verifier — already pre-pulled on the server, no new infra needed.
        var synthesisModel = string.IsNullOrWhiteSpace(_options.LocalVerifierModelTag)
            ? "qwen3:14b"
            : _options.LocalVerifierModelTag;

        var response = await _ollama.ChatJsonWithModelAsync(
            synthesisModel, system, Array.Empty<ChatMessage>(), user, ct, keepAlive: "0")
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response))
        {
            _log.LogDebug("Reflection synthesis returned empty response");
            return 0;
        }

        // Parse the structured JSON output. On parse failure, log and skip —
        // do NOT fall back to line-split (the v1 path), because that defeats
        // the entire purpose of R3 (forcing the model out of inner-thought
        // register via structural output constraint). A parse failure is a
        // signal to investigate the prompt or model, not to silently degrade
        // back to the old behavior.
        var observations = ParseReflectionSummaries(response);
        if (observations.Count == 0)
        {
            var preview = response.Length > 200 ? response[..200] + "..." : response;
            _log.LogWarning(
                "Reflection synthesis produced no usable summaries (parse failure or empty array). Raw response preview: {Preview}",
                preview);
            return 0;
        }

        var sourceIds = recentMemories.Select(m => m.Id).ToList();

        // Check ALL existing Semantic memories to avoid duplicating what we already know.
        // The reflection synthesis regenerates the same profile facts each cycle
        // ("About Mark: Learning Spanish", "Shared experience: Duck Norris").
        // Previous fix used GetRecentAsync(100) which missed existing records because
        // they weren't in the top 100 most recent across all types. Now queries
        // Semantic type directly — guaranteed to find all existing profile facts.
        var existingProfiles = (await _search.GetByTypeAsync(MemoryType.Semantic, 500, ct)
                .ConfigureAwait(false))
            .Select(m => m.Content.Length >= 50 ? m.Content[..50] : m.Content)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // J.5d (May 1, 2026) — gate evaluation context. Build once for the
        // whole batch (recent contact messages + recent Ani output) so each
        // observation is evaluated against the same conversational state.
        // Cheap to compute and identical across observations within a cycle.
        var gateContext = _outputGate is not null && _options.ReflectionOutputGateEnabled
            ? BuildGateContext(recentMemories, contact)
            : default;

        var saved = 0;
        var gateDropped = 0;
        var savedGistIds = new List<Guid>(); // Phase 6 v1.2 R2: tracked for soft-delete post-loop
        foreach (var observation in observations)
        {
            // Phase 6 v1.2 follow-on (2026-05-17 evening): the existingProfiles
            // 50-char-prefix dedup was originally designed to prevent old-shape
            // reflections from regenerating profile facts like "About Mark:
            // Learning Spanish" cycle after cycle. With v1.2-shape gists
            // (content prefix "[topic:") the same dedup over-fires because
            // many distinct gists share opening tokens ("[topic: warmth...",
            // "[topic: tender...") — empirically caused the 2026-05-17
            // 19:30 scan to terminate at iter=9 instead of draining the
            // 1654-record eligible pool. Fix: skip the prefix-dedup for
            // v1.2-shape output; each compression-cycle gist captures a
            // distinct slice and should not be deduped against prior gists.
            // Old-shape repetition prevention is preserved for legacy-path
            // observations.
            var prefix = observation.Length >= 50 ? observation[..50] : observation;
            var isV12Shape = observation.StartsWith("[topic:", StringComparison.OrdinalIgnoreCase);
            if (!isV12Shape && existingProfiles.Contains(prefix))
            {
                _log.LogDebug("Reflection: skipping duplicate '{Prefix}...'", prefix[..Math.Min(40, prefix.Length)]);
                continue;
            }

            // J.5d gate evaluation. Per-observation; on Remediate or Fail
            // we drop the observation (no regen — reflection observations
            // are independent, dropping the suspect line is safer than
            // re-rolling and getting a different fabrication). Other
            // observations from the same cycle that pass continue to save.
            if (_outputGate is not null && _options.ReflectionOutputGateEnabled)
            {
                var artifact = new CognitiveArtifact
                {
                    Content                 = observation,
                    ProducerKind            = CognitiveProducerKind.Reflection,
                    IntendedSink            = CognitiveOutputSink.PersistedSummary,
                    ContactName             = contact,
                    ContactRecentMessages   = gateContext.ContactMessages,
                    PriorAniMessages        = gateContext.AniMessages,
                };

                try
                {
                    var verdict = await _outputGate.EvaluateAsync(artifact, ct).ConfigureAwait(false);
                    if (verdict.Verdict != OutputGateVerdict.Pass)
                    {
                        _log.LogWarning(
                            "J.5d reflection gate {Verdict} [{Fired}] — dropping observation: \"{Preview}\" — hint: {Hint}",
                            verdict.Verdict, string.Join(",", verdict.FiredInvariants),
                            observation.Length > 80 ? observation[..80] + "..." : observation,
                            verdict.RemediationHint);
                        gateDropped++;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "J.5d reflection gate threw — saving observation uncovered (gate failure must NOT block reflection persistence).");
                }
            }

            // Phase 3 atomic save (2026-05-17, data-layer refactor).
            // EF path: atomic SaveReflectionGistAndCompressAsync — one
            // SaveChangesAsync transaction covers gist insert + source
            // tier UPDATEs + compressed_into link inserts. Fixes the v1
            // composition bug where Feature 30 dedup-merge orphaned the
            // gistId and caused MarkCompressedAsync's FK insert to roll
            // back the whole batch's source UPDATEs.
            //
            // Legacy path preserved as fallback while the new pattern
            // proves out in production.
            // F-3 U5 (2026-08-24) — pass the source records themselves
            // rather than just their IDs. Both gist-save paths now build
            // per-source ContentClaims from these records and wire them
            // through the ClaimBearingEmission envelope at the wrap site.
            var sourceRecordsForBatch = (IReadOnlyList<MemoryRecord>)recentMemories;
            Guid? savedGistId = null;
            var useEfPath = _options.UseEfReflectionGistService
                            && _gistService is not null
                            && _options.CompressionEnabled;

            if (useEfPath)
            {
                try
                {
                    savedGistId = await _gistService!.SaveReflectionGistAndCompressAsync(
                        observation, sourceRecordsForBatch, EpistemicTier.Interior, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "EfReflectionGistService failed for observation — falling back to legacy split path");
                }
            }

            if (savedGistId is null)
            {
                savedGistId = await SaveLegacyAsync(observation, sourceRecordsForBatch, ct)
                    .ConfigureAwait(false);
            }

            if (savedGistId is not null)
            {
                existingProfiles.Add(prefix);
                saved++;
                savedGistIds.Add(savedGistId.Value);
            }
        }

        _log.LogInformation(
            "Reflection synthesis: generated {Count} observations from {SourceCount} {SelectionMode} memories ({Saved} new, {Skipped} duplicates skipped, {GateDropped} gate-dropped, compression={Compression})",
            observations.Count,
            recentMemories.Count,
            _options.CompressionEnabled ? "decay-eligible" : "recent",
            saved,
            observations.Count - saved - gateDropped,
            gateDropped,
            _options.CompressionEnabled ? "on" : "off");

        return saved;
    }

    /// <summary>
    /// Legacy gist-save path — used when the EF reflection-gist service
    /// is not available (flag off or DI not wired). Composes
    /// <c>IMemoryPersistence.SaveAsync</c> + <c>MarkCompressedAsync</c>
    /// across method boundaries with separate transactions. Known to
    /// silently fail when Feature 30 dedup-merge orphans the gistId
    /// (see docs/spec/ANI-Data-Layer-UoW-Repository-Refactor-Plan.md).
    ///
    /// Returns the saved gistId on success, null on failure. Failure
    /// here is non-fatal: the caller logs and moves on to the next
    /// observation in the batch.
    /// </summary>
    private async Task<Guid?> SaveLegacyAsync(
        string observation,
        IReadOnlyList<MemoryRecord> sources,
        CancellationToken ct)
    {
        var gistId = Guid.NewGuid();
        var sourceGuids = sources.Where(m => m is not null).Select(m => m.Id).ToList();

        // F-2 Phase 1 P6 (2026-08-22) — reflection synthesis is Ani-composed
        // over source records; author verified, source descriptor points at
        // the reflection process (source-links to the underlying gists live
        // in the memory_links table separately).
        //
        // F-3 U5 (2026-08-24) — construct the composer emission first, then
        // project the attribution triple from it. When source records are
        // present, upgrade to ClaimBearingEmission so per-source attribution
        // flows through the envelope. Mirrors the EF path (see
        // EfReflectionGistService.BuildPerSourceClaims for the mechanical
        // claim-construction rationale).
        var now = DateTimeOffset.UtcNow;
        var perSourceClaims = ComposerEmissionExtensions.BuildPerSourceClaims(sources);
        IComposerEmission<string> gistEmission = perSourceClaims.Count > 0
            ? new ClaimBearingEmission<string>(
                Content:          observation,
                ComposerRole:     CognitiveProducerKind.Reflection,
                EmittedAt:        now,
                AttributedTo:     AttributedTo.Ani,
                AttributionTrust: "verified",
                Claims:           perSourceClaims)
            : ComposerEmissionExtensions.AniEmission(
                content:      observation,
                composerRole: CognitiveProducerKind.Reflection,
                emittedAt:    now);
        var reflectionAttribution = gistEmission.ToAttributionTriple();

        var record = new MemoryRecord
        {
            Id                = gistId,
            Type              = MemoryType.Semantic,
            Content           = gistEmission.Content,
            Importance        = 0.8f,
            RelationalValence = 0.5f,
            SourceName        = "reflection",
            Provenance        = EpistemicTier.Interior,
            AttributedTo               = reflectionAttribution.AttributedTo,
            AttributedAt               = reflectionAttribution.AttributedAt,
            AttributedSourceRecordId   = reflectionAttribution.SourceRecordId,
            AttributedSourceDescriptor = reflectionAttribution.SourceDescriptor,
            AttributionTrust           = reflectionAttribution.Trust,
        };

        try
        {
            await _persist.SaveAsync(record, ct).ConfigureAwait(false);
            record.LogAttribution(_log);
            if (perSourceClaims.Count > 0)
            {
                _log.LogInformation(
                    "F3_CLAIM_EMITTED producer=Reflection.Legacy gistId={GistId} claimsCount={Count}",
                    gistId, perSourceClaims.Count);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SaveLegacyAsync: SaveAsync failed for gist {GistId}", gistId);
            return null;
        }

        if (_options.CompressionEnabled && sourceGuids.Count > 0)
        {
            try
            {
                await _persist.MarkCompressedAsync(sourceGuids, gistId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "SaveLegacyAsync: MarkCompressedAsync failed for gist {GistId} (the legacy bug) — gist saved but sources may not be Compressed",
                    gistId);
                // Don't fail the operation — gist is durable.
            }
        }

        return gistId;
    }

    /// <summary>
    /// J.5d (May 1, 2026) — extract recent contact + ani-output messages
    /// from the recent-memory pool to seed the gate's confabulation
    /// classifier with conversational context. Mark-asserted records are
    /// identified by the canonical "Mark texted:" / "Mark said:" prefix
    /// or twilio-inbound source; Ani's outputs by "I said to Mark:" /
    /// "I reached out to Mark:" prefix or conversation source.
    /// </summary>
    internal static (IReadOnlyList<string> ContactMessages, IReadOnlyList<string> AniMessages) BuildGateContext(
        IReadOnlyList<MemoryRecord> recentMemories, string contact)
    {
        var contactMessages = new List<string>();
        var aniMessages = new List<string>();

        foreach (var m in recentMemories)
        {
            var content = m.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content)) continue;

            if (content.StartsWith($"{contact} texted:", StringComparison.OrdinalIgnoreCase)
             || content.StartsWith($"{contact} said:",   StringComparison.OrdinalIgnoreCase)
             || content.StartsWith("Mark texted:",       StringComparison.OrdinalIgnoreCase)
             || content.StartsWith("Mark said:",         StringComparison.OrdinalIgnoreCase))
            {
                contactMessages.Add(content);
            }
            else if (content.StartsWith("I said to ",      StringComparison.OrdinalIgnoreCase)
                  || content.StartsWith("I reached out to ", StringComparison.OrdinalIgnoreCase))
            {
                aniMessages.Add(content);
            }
        }

        // Cap each list at 8 to bound prompt size for the classifier.
        return (
            contactMessages.TakeLast(8).ToList(),
            aniMessages.TakeLast(8).ToList());
    }

    /// <summary>
    /// Phase 6 v1.2 R3 (May 17, 2026) — parses the structured JSON output of
    /// the reflection synthesis prompt. Expected shape:
    /// <code>
    /// { "summaries": [ {"topic": "...", "shape": "..."}, ... ] }
    /// </code>
    /// Each summary is rendered as a single line of memory content in the
    /// form <c>"[topic: {topic}] {shape}"</c> — short, register-tagged, free
    /// of verbatim source content.
    ///
    /// Returns an empty list on parse failure or empty summaries array.
    /// Caller is expected to log + skip on empty result (do NOT fall back to
    /// the v1 line-split behavior — that defeats the structural-output
    /// purpose of R3).
    /// </summary>
    internal static List<string> ParseReflectionSummaries(string jsonResponse)
    {
        if (string.IsNullOrWhiteSpace(jsonResponse))
            return new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            if (!doc.RootElement.TryGetProperty("summaries", out var summariesElement)
                || summariesElement.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            var results = new List<string>();
            foreach (var summary in summariesElement.EnumerateArray())
            {
                var topic = summary.TryGetProperty("topic", out var topicElement)
                    ? topicElement.GetString()?.Trim()
                    : null;
                var shape = summary.TryGetProperty("shape", out var shapeElement)
                    ? shapeElement.GetString()?.Trim()
                    : null;

                if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(shape))
                    continue;

                // Defensive cap on shape length — the prompt asks for ≤120 chars,
                // but the model can over-shoot. Hard-cap so a runaway summary
                // doesn't pollute substrate with verbatim source content
                // smuggled through the "shape" field.
                if (shape.Length > 200) shape = shape[..200];
                if (topic.Length > 80)  topic = topic[..80];

                results.Add($"[topic: {topic}] {shape}");
                if (results.Count >= 3) break;
            }

            return results;
        }
        catch (JsonException)
        {
            // Parse failure — return empty. Caller logs the raw response for
            // diagnostic and does not fall back to free-prose parsing.
            return new List<string>();
        }
    }
}