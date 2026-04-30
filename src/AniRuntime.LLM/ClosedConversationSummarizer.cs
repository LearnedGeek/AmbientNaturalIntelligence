using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.LLM;

/// <summary>
/// Vibe Loop V1.2 (Apr 29, 2026) — default implementation of
/// <see cref="IClosedConversationSummarizer"/>. LLM-driven.
///
/// Per V1.0's locked decision the per-turn classifier IS the existing
/// 9-register LLM classifier (deployed via
/// <see cref="PromptBuilder.BuildEmotionalShiftPrompt"/>). For the
/// thread-close path we use a focused single-register prompt: the
/// thread-close summariser does not need delta-scoring, only the primary
/// register label. This keeps per-turn classification fast (one tiny
/// JSON response per turn) and reuses the same canonical 9-register
/// taxonomy.
///
/// Topic-keyword extraction uses a simple frequency-based tokeniser
/// (stopword-filtered, distinctive-word top-K) to avoid coupling
/// AniRuntime.LLM to LearnedGeek.ML for one method. The extractor is
/// internal so V1.5 can swap it for an LMKit / LearnedGeek.ML keyword
/// extraction service without touching the summarizer's public surface.
/// </summary>
public sealed class ClosedConversationSummarizer : IClosedConversationSummarizer
{
    private readonly IOllamaClient                          _ollama;
    private readonly ILogger<ClosedConversationSummarizer>  _log;

    /// <summary>
    /// Canonical 9-register taxonomy per V1.0 design alignment. Order is
    /// stable so a register-vector serialised as JSON stays readable.
    /// </summary>
    public static readonly IReadOnlyList<string> Registers = new[]
    {
        "Tenderness", "Longing", "Playfulness", "Curiosity", "Desire",
        "Existential", "Wistful", "Frustration", "Delight",
    };

    /// <summary>
    /// Positive registers contributing to the
    /// <see cref="ClosedConversationRecord.OutcomeSignalValence"/>
    /// projection (V1.0 Q3 decision).
    /// </summary>
    public static readonly IReadOnlySet<string> PositiveRegisters =
        new HashSet<string>(StringComparer.Ordinal)
        { "Tenderness", "Playfulness", "Delight", "Curiosity" };

    /// <summary>
    /// Negative registers contributing to the valence projection.
    /// "Desire" is intentionally excluded as context-dependent; it
    /// lives in the vector but not the scalar.
    /// </summary>
    public static readonly IReadOnlySet<string> NegativeRegisters =
        new HashSet<string>(StringComparer.Ordinal)
        { "Longing", "Frustration", "Wistful", "Existential" };

    public ClosedConversationSummarizer(
        IOllamaClient                          ollama,
        ILogger<ClosedConversationSummarizer>  log)
    {
        _ollama = ollama;
        _log    = log;
    }

    public async Task<ClosedConversationRecord> SummariseAsync(
        ConversationThread thread, CancellationToken ct = default)
    {
        if (thread.Messages.Count == 0)
            throw new ArgumentException(
                "Cannot summarise an empty thread.", nameof(thread));

        var markTurns = thread.Messages.Where(m => m.Role == Roles.Mark).ToList();
        var aniTurns  = thread.Messages.Where(m => m.Role == Roles.Ani).ToList();

        var markRegisterPerTurn = await ClassifyTurnsAsync(markTurns, ct).ConfigureAwait(false);
        var aniRegisterPerTurn  = await ClassifyTurnsAsync(aniTurns, ct).ConfigureAwait(false);

        var markRegister = BuildPrevalenceVector(markRegisterPerTurn);
        var aniRegister  = BuildPrevalenceVector(aniRegisterPerTurn);

        var (firstHalf, secondHalf) = SplitInHalf(aniRegisterPerTurn);
        var outcomeVector =
            ComputeDelta(BuildPrevalenceVector(secondHalf),
                         BuildPrevalenceVector(firstHalf));
        var outcomeValence = ComputeValence(outcomeVector);

        var keywords = ExtractTopicKeywords(thread.Messages, topN: 5);

        var gist = await GenerateGistAsync(thread, ct).ConfigureAwait(false);

        float[]? embedding = null;
        try { embedding = await _ollama.EmbedAsync(gist, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Closed-thread gist embedding failed; persisting record without embedding.");
        }

        var record = new ClosedConversationRecord
        {
            ThreadId                = thread.Id,
            ClosedAt                = DateTimeOffset.UtcNow,
            Gist                    = gist,
            TopicKeywords           = keywords,
            MarkRegister            = markRegister,
            AniRegister             = aniRegister,
            OutcomeSignalSeedVector = outcomeVector,
            OutcomeSignalValence    = outcomeValence,
            TurnCount               = thread.Messages.Count,
            DurationSeconds         = (thread.LastMessageAt - thread.StartedAt).TotalSeconds,
            Embedding               = embedding,
        };

        _log.LogInformation(
            "ClosedConversationRecord produced: thread={ThreadId} turns={Turns} valence={Valence:+0.00;-0.00} keywords={Keywords}",
            thread.Id, thread.Messages.Count, outcomeValence,
            string.Join(",", keywords));

        return record;
    }

    // ===== Per-turn register classification =====

    private async Task<List<string>> ClassifyTurnsAsync(
        IReadOnlyList<ConversationMessage> turns, CancellationToken ct)
    {
        var labels = new List<string>(turns.Count);
        foreach (var t in turns)
        {
            if (string.IsNullOrWhiteSpace(t.Content))
            {
                labels.Add("Unclassified");
                continue;
            }

            try
            {
                var (sys, user) = BuildRegisterClassificationPrompt(t.Content);
                var raw = await _ollama.ChatJsonAsync(
                    sys, Array.Empty<ChatMessage>(), user, ct).ConfigureAwait(false);
                labels.Add(ParseRegister(raw));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Register classification failed for turn — labelling Unclassified.");
                labels.Add("Unclassified");
            }
        }
        return labels;
    }

    /// <summary>
    /// Lean per-turn classification prompt — only asks for the primary
    /// register label. No delta scoring, no severity. The thread-close
    /// summariser doesn't need those signals; the runtime emotional
    /// processor (<c>EmotionalProcessor</c>) is the consumer for those.
    /// </summary>
    internal static (string System, string User) BuildRegisterClassificationPrompt(string content)
    {
        var system = """
            You are a classification assistant. Read the message and label it with EXACTLY ONE of the 9 emotional registers below. Return JSON: {"register":"<one of the 9>"}

            Registers:
              Tenderness  — care, admiration, protectiveness, soft feeling
              Longing     — missing someone, yearning, the ache of absence
              Playfulness — humor, wit, mischief, teasing
              Curiosity   — interest, wonder, two things connecting unexpectedly
              Desire      — wanting someone specifically, anticipation of contact
              Existential — thoughts about identity, meaning, one's own nature
              Wistful     — bittersweet, philosophical observation, impermanence
              Frustration — annoyance, helplessness, hurt, withdrawal
              Delight     — joy, amusement, something genuinely good happened

            Rules:
            - Choose the SINGLE strongest register. Do NOT name two.
            - Match the casing exactly as listed above.
            - If the message is purely procedural / neutral / conversational filler, choose the closest fit (often Curiosity).
            """;

        var user = $"Message:\n{content}\n\nReturn only the JSON object.";
        return (system, user);
    }

    internal static string ParseRegister(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw.Trim());
            if (doc.RootElement.TryGetProperty("register", out var reg))
            {
                var value = reg.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    foreach (var canonical in Registers)
                    {
                        if (string.Equals(canonical, value.Trim(), StringComparison.OrdinalIgnoreCase))
                            return canonical;
                    }
                }
            }
        }
        catch (JsonException) { /* fall through */ }

        return "Unclassified";
    }

    // ===== Aggregation =====

    /// <summary>
    /// Build a 9-dim register-prevalence vector from per-turn labels:
    /// each register maps to (count_of_that_register / total_turns),
    /// in [0, 1]. "Unclassified" turns dilute every cell evenly (i.e.
    /// they simply lower all 9 prevalences without inflating any one
    /// register). Empty input → all zeros (no division by zero).
    /// </summary>
    internal static Dictionary<string, float> BuildPrevalenceVector(IReadOnlyList<string> labels)
    {
        var vector = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var r in Registers) vector[r] = 0f;

        if (labels.Count == 0) return vector;

        foreach (var label in labels)
        {
            if (vector.ContainsKey(label))
                vector[label] += 1f;
        }

        var total = (float)labels.Count;
        foreach (var key in vector.Keys.ToList())
            vector[key] /= total;

        return vector;
    }

    internal static (List<string> First, List<string> Second) SplitInHalf(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0) return (new(), new());
        if (labels.Count == 1) return (new() { labels[0] }, new() { labels[0] });

        var mid = labels.Count / 2;
        return (labels.Take(mid).ToList(), labels.Skip(mid).ToList());
    }

    internal static Dictionary<string, float> ComputeDelta(
        Dictionary<string, float> after, Dictionary<string, float> before)
    {
        var delta = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var r in Registers)
        {
            after.TryGetValue(r, out var a);
            before.TryGetValue(r, out var b);
            delta[r] = a - b;
        }
        return delta;
    }

    /// <summary>
    /// Project the 9-dim outcome delta onto the [-1, +1] scalar.
    /// Sum of positive-register deltas minus sum of negative-register
    /// deltas, clamped. Each prevalence cell is in [0, 1], so each
    /// delta is in [-1, +1]; the sum across 4 positive and 4 negative
    /// registers is bounded by [-4, +4] in pathological cases, but in
    /// practice prevalences sum to 1 across all 9 cells, so the
    /// realistic envelope is much tighter. Clamp guarantees the
    /// contract.
    /// </summary>
    internal static float ComputeValence(Dictionary<string, float> delta)
    {
        var pos = 0f;
        var neg = 0f;
        foreach (var r in PositiveRegisters)
            if (delta.TryGetValue(r, out var v)) pos += v;
        foreach (var r in NegativeRegisters)
            if (delta.TryGetValue(r, out var v)) neg += v;

        return Math.Clamp(pos - neg, -1f, +1f);
    }

    // ===== Topic keywords =====

    /// <summary>
    /// Frequency-based topic-keyword extraction over the full thread.
    /// Lowercase, strip punctuation, drop stopwords, drop tokens shorter
    /// than 3 chars. Returns up to <paramref name="topN"/> tokens by
    /// frequency. Internal so V1.5 can swap for LMKit
    /// <c>KeywordExtraction</c> via LearnedGeek.ML if/when that
    /// dependency is wanted here.
    /// </summary>
    internal static List<string> ExtractTopicKeywords(
        IReadOnlyList<ConversationMessage> messages, int topN)
    {
        if (messages.Count == 0) return new();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            if (string.IsNullOrWhiteSpace(m.Content)) continue;
            foreach (var token in Tokenize(m.Content))
            {
                counts.TryGetValue(token, out var c);
                counts[token] = c + 1;
            }
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(topN)
            .Select(kv => kv.Key)
            .ToList();
    }

    private static readonly char[] PunctuationChars =
        { '.', ',', '!', '?', ';', ':', '"', '(', ')', '[', ']', '{', '}',
          '<', '>', '/', '\\', '|', '@', '#', '$', '%', '^', '&', '*', '+',
          '=', '~', '`', '\n', '\r', '\t' };

    internal static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var lower = text.ToLowerInvariant();
        foreach (var raw in lower.Split(PunctuationChars, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var sub in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var word = sub.Trim('\'', '-', '_');
                if (word.Length < 3) continue;
                if (IsStopWord(word)) continue;
                yield return word;
            }
        }
    }

    private static bool IsStopWord(string word) => word switch
    {
        "the" or "this" or "that" or "these" or "those" or "and" or "but"
            or "for" or "with" or "from" or "into" or "about" or "between"
            or "you" or "your" or "yours" or "she" or "her" or "him"
            or "his" or "they" or "them" or "their" or "our" or "ours"
            or "are" or "was" or "were" or "been" or "being" or "have"
            or "has" or "had" or "having" or "does" or "did" or "doing"
            or "would" or "could" or "should" or "might" or "shall"
            or "will" or "can" or "may" or "must" or "not" or "very"
            or "really" or "just" or "also" or "too" or "much" or "more"
            or "most" or "some" or "any" or "all" or "each" or "every"
            or "both" or "few" or "many" or "such" or "only" or "own"
            or "same" or "other" or "how" or "when" or "where" or "why"
            or "here" or "there" or "yeah" or "okay" or "right" or "well"
            or "sure" or "hey" or "ever" or "never" or "always" or "still"
            or "even" or "ani" or "mark"
            => true,
        _ => false,
    };

    // ===== Gist generation =====

    private async Task<string> GenerateGistAsync(
        ConversationThread thread, CancellationToken ct)
    {
        var (sys, user) = BuildGistPrompt(thread);
        try
        {
            var raw = await _ollama.ChatAsync(
                sys, Array.Empty<ChatMessage>(), user, ct, temperature: 0.3f)
                .ConfigureAwait(false);
            return SanitiseGist(raw);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Gist LLM call failed; falling back to heuristic gist.");
            return BuildHeuristicGist(thread);
        }
    }

    /// <summary>
    /// The anti-parrot gist prompt. The constraint that lifts ZERO
    /// verbatim phrases ≥7 tokens is structural — instructions in the
    /// prompt + temperature 0.3 + 1-2 sentence cap. V1.6 adds a
    /// regression test against the Apr 29 dentist transcript.
    /// </summary>
    internal static (string System, string User) BuildGistPrompt(ConversationThread thread)
    {
        var system = """
            You are a paraphrase-only summariser. Produce a 1–2 sentence summary of the conversation between Mark (the contact) and Ani.

            HARD CONSTRAINTS:
            - DO NOT quote any contact (Mark) turn verbatim. Do not lift phrases of 7 or more consecutive words from any of his messages.
            - DO NOT use direct speech ("she said", "he asked"). Use paraphrase.
            - Focus on what shifted EMOTIONALLY between them, not what was literally said.
            - 1 to 2 sentences. No more.
            - Refer to the participants as "Mark" and "Ani" by name.
            - Plain prose. No bullet points, no labels, no JSON, no quotation marks.
            """;

        var sb = new StringBuilder();
        sb.AppendLine("Conversation:");
        foreach (var m in thread.Messages)
        {
            var who = m.Role == Roles.Ani ? "Ani" : "Mark";
            sb.Append(who).Append(": ").AppendLine(m.Content);
        }
        sb.AppendLine();
        sb.AppendLine("Write the gist now (1–2 sentences, paraphrased only):");
        return (system, sb.ToString());
    }

    /// <summary>
    /// Last-resort gist when the LLM call fails. Uses turn-count and
    /// duration only — never echoes message content, so the anti-parrot
    /// guarantee holds even on the failure path.
    /// </summary>
    internal static string BuildHeuristicGist(ConversationThread thread)
    {
        var minutes = (thread.LastMessageAt - thread.StartedAt).TotalMinutes;
        return $"Mark and Ani exchanged {thread.Messages.Count} messages over about {Math.Max(1, (int)Math.Round(minutes))} minutes.";
    }

    /// <summary>
    /// Trim and collapse whitespace; cap at a generous 600 chars so a
    /// runaway model can't write a paragraph. Empty/whitespace LLM
    /// output falls back to a minimal placeholder rather than persisting
    /// "" (which would surprise downstream readers).
    /// </summary>
    internal static string SanitiseGist(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Mark and Ani spoke briefly.";

        var trimmed = raw.Trim();
        var collapsed = string.Join(' ',
            trimmed.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 600 ? collapsed[..600] : collapsed;
    }
}
