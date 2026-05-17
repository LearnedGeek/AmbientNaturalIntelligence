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
    private readonly ILogger<ReflectionPhase> _log;
    private int _cyclesSinceLastReflection;

    public ReflectionPhase(
        IOllamaClient ollama,
        IMemoryPersistence persist,
        IMemorySearch search,
        IOptions<AniOptions> options,
        ILogger<ReflectionPhase> log,
        ICognitiveOutputGate? outputGate = null)
    {
        _ollama = ollama;
        _persist = persist;
        _search = search;
        _options = options.Value;
        _outputGate = outputGate;
        _log = log;
    }

    /// <summary>
    /// Increments the cycle counter and runs reflection if the interval is reached.
    /// Returns true if reflection was performed this cycle.
    /// </summary>
    public async Task<bool> TryRunAsync(
        CharacterStateDoc characterState, CancellationToken ct)
    {
        if (!_options.ReflectionEnabled) return false;

        _cyclesSinceLastReflection++;
        if (_cyclesSinceLastReflection < _options.ReflectionCycleInterval)
            return false;

        _cyclesSinceLastReflection = 0;

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

    private async Task RunReflectionAsync(
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
            return;
        }

        var contact = characterState.PrimaryContactName ?? "Mark";
        var memoryContents = recentMemories.Select(m => m.Content).ToList();

        var (system, user) = PromptBuilder.BuildReflectionSynthesisPrompt(
            characterState.Name, contact, memoryContents);

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
            return;
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
            return;
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
            // Skip if we already have a reflection memory with this prefix
            var prefix = observation.Length >= 50 ? observation[..50] : observation;
            if (existingProfiles.Contains(prefix))
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

            // Phase 6 v1.2 R2 (May 17, 2026): assign an Id up-front so we can
            // pass it to MarkCompressedAsync after save. SaveAsync may merge
            // this record into an existing one (Feature 30), in which case
            // the merge target's Id supersedes — but for new synthesis
            // records (the common case for reflection gists, which are
            // novel content not duplicating prior facts), the assigned Id
            // is what persists.
            var gistId = Guid.NewGuid();
            var record = new MemoryRecord
            {
                Id = gistId,
                Type = MemoryType.Semantic,
                Content = observation,
                Importance = 0.8f,
                RelationalValence = 0.5f,
                SourceName = "reflection",
                // Epistemic Grounding (Apr 10): Reflections are syntheses of prior
                // memory into higher-level self-observations — Interior tier. These
                // inform Ani's self-model but are not factual assertions about Mark's world.
                Provenance = EpistemicTier.Interior,
            };

            await _persist.SaveAsync(record, ct).ConfigureAwait(false);
            existingProfiles.Add(prefix); // Prevent saving duplicates within same batch
            saved++;
            savedGistIds.Add(gistId);
        }

        // Phase 6 v1.2 R2 (May 17, 2026): soft-delete source records into the
        // gists we just saved. If at least one gist saved, mark all source
        // records as Compressed and create compressed_into links for
        // provenance. The compression is keyed on the first saved gist for
        // the link target — multiple gists in one cycle share the same
        // sources because they came from the same synthesis input. Future
        // refinement could split sources by gist if topic-clustering is
        // explicit; current v1.2 keeps it batch-shared because the LLM does
        // implicit clustering across the 10-record input.
        if (_options.CompressionEnabled && savedGistIds.Count > 0)
        {
            try
            {
                var sourceGuids = recentMemories.Select(m => m.Id).ToList();
                await _persist.MarkCompressedAsync(sourceGuids, savedGistIds[0], ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Phase 6 v1.2 MarkCompressedAsync failed — gist(s) saved but sources NOT marked Compressed. " +
                    "Sources will continue to appear in retrieval until next compression cycle. " +
                    "Soft-delete is not blocking; the gist is the durable artifact.");
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