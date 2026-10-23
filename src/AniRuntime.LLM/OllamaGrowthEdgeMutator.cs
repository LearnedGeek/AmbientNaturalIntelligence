using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
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
    private readonly IRegisterClassifier?               _registerClassifier;
    private readonly ILogger<OllamaGrowthEdgeMutator>   _log;

    public OllamaGrowthEdgeMutator(
        IOllamaClient                       ollama,
        IOptions<AniOptions>                options,
        ILogger<OllamaGrowthEdgeMutator>    log,
        IRegisterClassifier?                registerClassifier = null)
    {
        _ollama             = ollama  ?? throw new ArgumentNullException(nameof(ollama));
        _options            = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _registerClassifier = registerClassifier;
        _log                = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<GrowthEdgeUpdateResult> UpdateTopicValenceAsync(
        CharacterStateDoc characterState,
        IReadOnlyList<MemoryRecord> recentThoughts,
        CancellationToken ct,
        IReadOnlyList<string>? recentAnchors = null)
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

        // F-4 Phase 4a.2c: merge anchor-promoted candidates into the extracted
        // list before ApplyUpdates. Anchors passed in from ReflectionPhase
        // reflect what she's actually been returning to over the recent
        // window; the LLM pathway rounds out with topic-salience from
        // free-form thought content. If both pathways name the same key,
        // bounded-delta discipline in ApplyUpdates still caps the total
        // per-cycle change (iterations compose, no double-scoring guard
        // needed — the max-delta is per-call-to-ApplyUpdates).
        var anchorCount = 0;
        if (_options.AnchorPromotionEnabled
            && recentAnchors is not null
            && recentAnchors.Count > 0)
        {
            var promoted = AnchorPromotionScoring.Score(
                recentAnchors:   recentAnchors,
                existingKeys:    existingKeys,
                minOccurrences:  _options.AnchorPromotionMinOccurrences);

            if (promoted.Count > 0)
            {
                var salience = Math.Clamp(_options.AnchorPromotionSalience, 0f, 1f);
                var merged   = new List<ExtractedTopic>(extracted);
                foreach (var p in promoted)
                {
                    merged.Add(new ExtractedTopic(p.Topic, salience, p.ExistingKeyMatch));
                }
                extracted = merged;
                anchorCount = promoted.Count;
                _log.LogInformation(
                    "F4_ANCHOR_PROMOTION promoted={Count} windowSize={Window} minOccur={Min}",
                    promoted.Count, recentAnchors.Count, _options.AnchorPromotionMinOccurrences);
            }
        }

        if (extracted.Count == 0)
        {
            _log.LogDebug(
                "F4_GROWTH_EDGE_UPDATE no topics extracted (empty or malformed JSON; anchors promoted={Anchor})",
                anchorCount);
            return GrowthEdgeUpdateResult.Empty;
        }

        return ApplyUpdates(characterState, extracted);
    }

    public async Task<GrowthEdgeUpdateResult> UpdateToneValenceAsync(
        CharacterStateDoc characterState,
        IReadOnlyList<MemoryRecord> recentMarkReplies,
        CancellationToken ct)
    {
        if (characterState is null) throw new ArgumentNullException(nameof(characterState));
        if (recentMarkReplies is null || recentMarkReplies.Count == 0)
            return GrowthEdgeUpdateResult.Empty;
        if (_registerClassifier is null)
        {
            _log.LogDebug(
                "F4_TONE_UPDATE skipped — no IRegisterClassifier wired");
            return GrowthEdgeUpdateResult.Empty;
        }

        // Classify each Mark reply via the already-live register classifier,
        // then group by canonical register-family. Each tone descriptor's
        // salience is its share of the recent window (frequency as proxy
        // for Mark's current register preference).
        var registerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var classified = 0;
        var scoreable = recentMarkReplies
            .Where(r => !string.IsNullOrWhiteSpace(r.Content));
        foreach (var reply in scoreable)
        {
            string register;
            try
            {
                register = await _registerClassifier.ClassifyAsync(reply.Content, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            if (string.IsNullOrWhiteSpace(register) ||
                string.Equals(register, "Unclassified", StringComparison.OrdinalIgnoreCase))
                continue;

            var toneKey = ImpactCategoryDefaults.ToRegisterFamily(register)
                .ToString().ToLowerInvariant();
            registerCounts[toneKey] = registerCounts.TryGetValue(toneKey, out var n) ? n + 1 : 1;
            classified++;
        }

        if (classified == 0)
        {
            _log.LogDebug("F4_TONE_UPDATE no classifiable Mark replies in window");
            return GrowthEdgeUpdateResult.Empty;
        }

        // Convert counts to extracted-topic shape so ApplyUpdates discipline
        // (bounded delta, new-key threshold, prune, soft cap) applies
        // identically to ToneValence. Salience = count / classified so a
        // register that dominates the window carries higher weight.
        var existingKeys = new HashSet<string>(characterState.ToneValence.Keys,
            StringComparer.OrdinalIgnoreCase);
        var topics = registerCounts
            .Select(kv =>
            {
                var salience = (float)kv.Value / classified;
                var match = existingKeys.FirstOrDefault(k =>
                    string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase));
                return new ExtractedTopic(kv.Key, salience, match);
            })
            .ToList();

        return ApplyUpdatesToDictionary(characterState.ToneValence, topics, "ToneValence");
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
        => ApplyUpdatesToDictionary(characterState.TopicValence, extracted, "TopicValence");

    /// <summary>
    /// Shared bounded-delta mutation applied to any growth-edge dictionary.
    /// Phase 1.1 (2026-10-23): extracted from the TopicValence-specific body
    /// so ToneValence can reuse the discipline. Caller supplies the dict
    /// (<c>TopicValence</c> or <c>ToneValence</c>) and a <paramref name="logTag"/>
    /// used in telemetry so journal lines distinguish which growth edge moved.
    /// </summary>
    internal GrowthEdgeUpdateResult ApplyUpdatesToDictionary(
        Dictionary<string, float> dict,
        IReadOnlyList<ExtractedTopic> extracted,
        string logTag)
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
                && dict.TryGetValue(topic.ExistingKeyMatch!, out var current))
            {
                // Bounded delta toward (not past) 1.0, scaled by salience
                var delta = salience * maxDelta;
                var updated = Math.Clamp(current + delta, 0f, 1f);
                var actualDelta = updated - current;
                if (Math.Abs(actualDelta) > 1e-6f)
                {
                    dict[topic.ExistingKeyMatch!] = updated;
                    changes.Add((topic.ExistingKeyMatch!, actualDelta));
                    keysUpdated++;
                }
            }
            else if (!hasMatchClaim
                     && salience >= newKeyThreshold
                     && !string.IsNullOrWhiteSpace(topic.Topic)
                     && !dict.ContainsKey(topic.Topic))
            {
                // Strict discipline: a non-null-but-invalid existing_key_match
                // is treated as model hallucination and the whole signal is
                // dropped (we don't trust the new-key extraction either when
                // the match claim was already wrong). Only emit a new key
                // when the model explicitly declined to match (null match).
                var initial = Math.Clamp(salience * newKeyBase, 0f, 1f);
                dict[topic.Topic] = initial;
                changes.Add((topic.Topic, initial));
                newKeysAdded++;
            }
        }

        // Prune pass: drop keys that have decayed below the threshold.
        // Note: this phase doesn't decay keys itself (no negative deltas),
        // so prune mostly serves future expansion when decay is added, plus
        // tidying any key that was seeded very low.
        var pruned = new List<string>();
        var toPrune = dict
            .Where(kv => kv.Value < pruneThreshold)
            .ToList();
        foreach (var kv in toPrune)
        {
            dict.Remove(kv.Key);
            pruned.Add(kv.Key);
        }

        // Soft cap: if we exceeded MaxKeys after this cycle's inserts,
        // trim the lowest-weighted entries so the dictionary doesn't grow
        // unbounded. Seeded keys with high weight are protected by weight;
        // noise keys that got added once and never reinforced trim first.
        if (dict.Count > maxKeys)
        {
            var trim = dict.Count - maxKeys;
            var toTrim = dict
                .OrderBy(kv => kv.Value)
                .Take(trim)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in toTrim)
            {
                dict.Remove(k);
                pruned.Add(k);
            }
        }

        _log.LogInformation(
            "F4_GROWTH_EDGE_UPDATE edge={Edge} updated={Updated} added={Added} pruned={Pruned} dictSize={Size}",
            logTag, keysUpdated, newKeysAdded, pruned.Count, dict.Count);

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

            foreach (var el in topics.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var topic = el.TryGetProperty("topic", out var t) && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? string.Empty).Trim()
                    : string.Empty;

                var salience = el.TryGetProperty("salience", out var s)
                               && s.ValueKind is JsonValueKind.Number
                    ? (float)s.GetDouble()
                    : 0f;

                string? match = null;
                if (el.TryGetProperty("existing_key_match", out var m)
                    && m.ValueKind == JsonValueKind.String)
                {
                    var v = (m.GetString() ?? string.Empty).Trim();
                    if (v.Length > 0) match = v;
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
