using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// F-4 Phase 1 (2026-10-22) — canonical implementation of
/// <see cref="IGrowthEdgeMutator"/> using qwen3:14b (configurable via
/// <see cref="AniOptions.GrowthEdgeMutatorModel"/>). Mirrors the shape of
/// <see cref="QwenRegisterClassifier"/>: one prompt, one model, JSON-mode
/// transport, fail-open on transport/parse errors.
///
/// <para>
/// Uses the local utility model rather than ani-v7 because the task is
/// topic-extraction + salience-scoring over source text, not voice-aligned
/// generation. Same rationale as the switch of
/// <c>ReflectionPhase.RunReflectionAsync</c> from <c>ani-v7-inner</c> to
/// Qwen in May 2026 — fine-tune register fights structural-output tasks.
/// </para>
/// </summary>
public sealed class OllamaGrowthEdgeMutator : IGrowthEdgeMutator
{
    private readonly IOllamaClient                      _ollama;
    private readonly AniOptions                         _options;
    private readonly ILogger<OllamaGrowthEdgeMutator>   _log;

    public OllamaGrowthEdgeMutator(
        IOllamaClient                       ollama,
        IOptions<AniOptions>                options,
        ILogger<OllamaGrowthEdgeMutator>    log)
    {
        _ollama  = ollama  ?? throw new ArgumentNullException(nameof(ollama));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<GrowthEdgeUpdateResult> UpdateTopicValenceAsync(
        CharacterStateDoc characterState,
        IReadOnlyList<MemoryRecord> recentThoughts,
        CancellationToken ct)
    {
        if (characterState is null) throw new ArgumentNullException(nameof(characterState));
        if (recentThoughts is null || recentThoughts.Count == 0)
            return GrowthEdgeUpdateResult.Empty;

        var model = string.IsNullOrWhiteSpace(_options.GrowthEdgeMutatorModel)
            ? "qwen3:14b"
            : _options.GrowthEdgeMutatorModel;

        var existingKeys = characterState.TopicValence.Keys.ToList();
        var userPrompt   = BuildUserPrompt(recentThoughts, existingKeys);

        string raw;
        try
        {
            raw = await _ollama.ChatJsonWithModelAsync(
                model:        model,
                systemPrompt: SystemPrompt,
                history:      Array.Empty<ChatMessage>(),
                userMessage:  userPrompt,
                ct:           ct,
                keepAlive:    "0")
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "F4_GROWTH_EDGE_UPDATE transport failure — returning empty result; dictionary unchanged");
            return GrowthEdgeUpdateResult.Empty;
        }

        var extracted = ParseExtractedTopics(raw);
        if (extracted.Count == 0)
        {
            _log.LogDebug("F4_GROWTH_EDGE_UPDATE no topics extracted (empty or malformed JSON)");
            return GrowthEdgeUpdateResult.Empty;
        }

        return ApplyUpdates(characterState, extracted);
    }

    /// <summary>
    /// Pure function: given a parsed list of extracted topics, mutates
    /// <paramref name="characterState"/>'s TopicValence dictionary in-place
    /// with bounded deltas. Extracted here as internal so unit tests can
    /// exercise the mutation logic without an Ollama transport.
    /// </summary>
    internal GrowthEdgeUpdateResult ApplyUpdates(
        CharacterStateDoc characterState,
        IReadOnlyList<ExtractedTopic> extracted)
    {
        var maxDelta = _options.GrowthEdgeMaxDeltaPerCycle;
        var newKeyThreshold = _options.GrowthEdgeNewKeyThreshold;
        var newKeyBase = _options.GrowthEdgeNewKeyBaseWeight;
        var pruneThreshold = _options.GrowthEdgePruneThreshold;
        var maxKeys = _options.GrowthEdgeMaxKeys;

        var changes = new List<(string Key, float Delta)>();
        var keysUpdated = 0;
        var newKeysAdded = 0;

        foreach (var topic in extracted)
        {
            var salience = Math.Clamp(topic.Salience, 0f, 1f);
            var hasMatchClaim = !string.IsNullOrWhiteSpace(topic.ExistingKeyMatch);

            if (hasMatchClaim
                && characterState.TopicValence.TryGetValue(topic.ExistingKeyMatch!, out var current))
            {
                // Bounded delta toward (not past) 1.0, scaled by salience
                var delta = salience * maxDelta;
                var updated = Math.Clamp(current + delta, 0f, 1f);
                var actualDelta = updated - current;
                if (Math.Abs(actualDelta) > 1e-6f)
                {
                    characterState.TopicValence[topic.ExistingKeyMatch!] = updated;
                    changes.Add((topic.ExistingKeyMatch!, actualDelta));
                    keysUpdated++;
                }
            }
            else if (!hasMatchClaim
                     && salience >= newKeyThreshold
                     && !string.IsNullOrWhiteSpace(topic.Topic)
                     && !characterState.TopicValence.ContainsKey(topic.Topic))
            {
                // Strict discipline: a non-null-but-invalid existing_key_match
                // is treated as model hallucination and the whole signal is
                // dropped (we don't trust the new-key extraction either when
                // the match claim was already wrong). Only emit a new key
                // when the model explicitly declined to match (null match).
                var initial = Math.Clamp(salience * newKeyBase, 0f, 1f);
                characterState.TopicValence[topic.Topic] = initial;
                changes.Add((topic.Topic, initial));
                newKeysAdded++;
            }
        }

        // Prune pass: drop keys that have decayed below the threshold.
        // Note: this phase doesn't decay keys itself (no negative deltas),
        // so prune mostly serves future expansion when decay is added, plus
        // tidying any key that was seeded very low.
        var pruned = new List<string>();
        foreach (var kv in characterState.TopicValence.ToList())
        {
            if (kv.Value < pruneThreshold)
            {
                characterState.TopicValence.Remove(kv.Key);
                pruned.Add(kv.Key);
            }
        }

        // Soft cap: if we exceeded MaxKeys after this cycle's inserts,
        // trim the lowest-weighted entries so the dictionary doesn't grow
        // unbounded. Seeded keys with high weight are protected by weight;
        // noise keys that got added once and never reinforced trim first.
        if (characterState.TopicValence.Count > maxKeys)
        {
            var trim = characterState.TopicValence.Count - maxKeys;
            var toTrim = characterState.TopicValence
                .OrderBy(kv => kv.Value)
                .Take(trim)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in toTrim)
            {
                characterState.TopicValence.Remove(k);
                pruned.Add(k);
            }
        }

        _log.LogInformation(
            "F4_GROWTH_EDGE_UPDATE updated={Updated} added={Added} pruned={Pruned} dictSize={Size}",
            keysUpdated, newKeysAdded, pruned.Count, characterState.TopicValence.Count);

        return new GrowthEdgeUpdateResult(
            KeysUpdated: keysUpdated,
            NewKeysAdded: newKeysAdded,
            KeysPruned: pruned.Count,
            Changes: changes);
    }

    private const string SystemPrompt = """
        You are a topic-salience extractor.

        INPUT: a short list of someone's recent inner-thought records plus the list of topic-keys she has previously cared about (her "growth edges").

        TASK: extract the topics that are actually present in the thoughts, with a salience score 0.0-1.0 reflecting how central each topic is across the batch. If a topic is a close conceptual match to an existing key, name that key in `existing_key_match`. Otherwise leave `existing_key_match` null and give the topic a short lowercase phrase.

        TAXONOMY GUIDANCE:
        - "topic" means a specific referent (a person, a place, an activity, an interest, a current event). Not a mood ("warmth"), not a register ("longing"), not a sensation ("cold").
        - Named entities (people, places, specific books, specific songs, specific events) are high-signal.
        - Generic scene elements ("the couch", "the kitchen", "the quiet corner") are LOW signal — those are register, not topic. Do not emit them unless they anchor a specific referent ("Okauchee Lake", "the bookstore mystery section").
        - Prefer existing_key_match over new keys when the fit is close (same person, same activity, same place). Only emit a new key when the topic is genuinely absent from the existing list.

        FORMAT: return STRICT JSON of the form:
        {"topics": [{"topic": "...", "salience": 0.0-1.0, "existing_key_match": "..." or null}, ...]}

        Return at most 10 topics. If no topic rises above 0.3 salience in the batch, return {"topics": []}.
        """;

    private static string BuildUserPrompt(
        IReadOnlyList<MemoryRecord> recentThoughts,
        IReadOnlyList<string> existingKeys)
    {
        var sb = new StringBuilder();
        sb.AppendLine("EXISTING TOPIC KEYS she has previously cared about:");
        if (existingKeys.Count == 0)
        {
            sb.AppendLine("  (none yet)");
        }
        else
        {
            foreach (var k in existingKeys)
                sb.AppendLine($"  - {k}");
        }
        sb.AppendLine();
        sb.AppendLine("RECENT INNER THOUGHTS (last batch she reflected on):");
        for (var i = 0; i < recentThoughts.Count; i++)
        {
            var content = recentThoughts[i].Content ?? string.Empty;
            if (content.Length > 400) content = content[..400] + "…";
            sb.AppendLine($"  [{i + 1}] {content}");
        }
        sb.AppendLine();
        sb.AppendLine("Extract topics with salience and existing_key_match per the taxonomy above. Return JSON only.");
        return sb.ToString();
    }

    /// <summary>Parsed topic record. Internal so tests can construct fixture arrays.</summary>
    internal sealed record ExtractedTopic(string Topic, float Salience, string? ExistingKeyMatch);

    internal static List<ExtractedTopic> ParseExtractedTopics(string raw)
    {
        var result = new List<ExtractedTopic>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        try
        {
            using var doc = JsonDocument.Parse(raw.Trim());
            if (!doc.RootElement.TryGetProperty("topics", out var topics)
                || topics.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var el in topics.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var topic = el.TryGetProperty("topic", out var t) && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? string.Empty).Trim()
                    : string.Empty;

                var salience = el.TryGetProperty("salience", out var s)
                               && s.ValueKind is JsonValueKind.Number
                    ? (float)s.GetDouble()
                    : 0f;

                string? match = null;
                if (el.TryGetProperty("existing_key_match", out var m))
                {
                    if (m.ValueKind == JsonValueKind.String)
                    {
                        var v = (m.GetString() ?? string.Empty).Trim();
                        if (v.Length > 0) match = v;
                    }
                }

                if (!string.IsNullOrWhiteSpace(topic) || match is not null)
                {
                    result.Add(new ExtractedTopic(topic, salience, match));
                }
            }
        }
        catch (JsonException)
        {
            // Fail-open — caller logs via GrowthEdgeUpdateResult.Empty
        }

        return result;
    }
}
