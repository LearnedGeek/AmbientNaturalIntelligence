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
        // Retrieve recent non-reflection memories
        var recentMemories = (await _persist.GetRecentAsync(10, ct).ConfigureAwait(false)).ToList();
        if (recentMemories.Count < 3)
        {
            _log.LogDebug("Reflection skipped — only {Count} recent memories (need at least 3)", recentMemories.Count);
            return;
        }

        var contact = characterState.PrimaryContactName ?? "Mark";
        var memoryContents = recentMemories.Select(m => m.Content).ToList();

        var (system, user) = PromptBuilder.BuildReflectionSynthesisPrompt(
            characterState.Name, contact, memoryContents);

        var response = await _ollama.InnerMonologueChatAsync(
            system, Array.Empty<ChatMessage>(), user, ct, keepAlive: "0")
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response))
        {
            _log.LogDebug("Reflection synthesis returned empty response");
            return;
        }

        // Parse observations (one per line, skip empty)
        var observations = response
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 10) // Skip very short lines
            .Take(3)
            .ToList();

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

            var record = new MemoryRecord
            {
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
        }

        _log.LogInformation(
            "Reflection synthesis: generated {Count} observations from {SourceCount} recent memories ({Saved} new, {Skipped} duplicates skipped, {GateDropped} gate-dropped)",
            observations.Count, recentMemories.Count, saved, observations.Count - saved - gateDropped, gateDropped);
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
}