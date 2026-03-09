using System.Text.Json;
using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Ani's full cognitive cycle, executed once per scheduled wake.
///
/// Phase sequence:
///   1. Perception  — poll all enabled sources since last cycle
///   2. Context     — build snapshot once, share across all phases
///   3. Inner thought — private LLM call; score Mark valence; persist
///   4. Desire update — apply temporal drift and trigger weights
///   5. Outreach    — conditional on desire threshold; dispatch or cooldown
///
/// Constructor is kept to 5 dependencies per code quality standards.
/// PromptBuilder is stateless and called statically.
/// Perception sources are injected as IEnumerable<IPerceptionSource>.
/// </summary>
public class CognitiveCycleProcessor
{
    private readonly IMemoryService                  _memory;
    private readonly IOllamaClient                   _ollama;
    private readonly DesireEngine                    _desire;
    private readonly AniActionDispatcher             _dispatcher;
    private readonly IEnumerable<IPerceptionSource>  _sources;
    private readonly AniOptions                      _aniOptions;
    private readonly ILogger<CognitiveCycleProcessor> _log;

    private DateTimeOffset _lastCycleAt = DateTimeOffset.UtcNow;

    public CognitiveCycleProcessor(
        IMemoryService                 memory,
        IOllamaClient                  ollama,
        DesireEngine                   desire,
        AniActionDispatcher            dispatcher,
        IEnumerable<IPerceptionSource> sources,
        IOptions<AniOptions>           aniOptions,
        ILogger<CognitiveCycleProcessor> log)
    {
        _memory     = memory;
        _ollama     = ollama;
        _desire     = desire;
        _dispatcher = dispatcher;
        _sources    = sources;
        _aniOptions = aniOptions.Value;
        _log        = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogDebug("Cognitive cycle starting");

        // Phase 1: Perception
        var perceptions = await PollPerceptionSourcesAsync(ct).ConfigureAwait(false);

        // Phase 2: Context snapshot — built once, shared across all phases
        var snapshot = await BuildContextSnapshotAsync(perceptions, ct).ConfigureAwait(false);

        // Phase 3: Inner thought
        var (thought, valence) = await RunInnerThoughtAsync(snapshot, ct).ConfigureAwait(false);

        await _memory.SaveAsync(new MemoryRecord
        {
            Type        = MemoryType.InnerThought,
            Content     = thought,
            MarkValence = valence,
            Importance  = valence > (float)_aniOptions.ValenceTriggerThreshold ? 0.8f : 0.3f,
            OccurredAt  = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        _log.LogInformation("Inner thought (valence={Valence:F2}): {Thought}",
            valence, thought);

        // Phase 4: Desire update
        await _desire.ApplyDriftAsync(ct).ConfigureAwait(false);

        if (valence > (float)_aniOptions.ValenceTriggerThreshold)
            await _desire.AddTriggerAsync(
                TriggerType.SpontaneousThought, valence,
                $"thought: {thought[..Math.Min(60, thought.Length)]}", ct).ConfigureAwait(false);

        // Phase 5: Outreach — only if desire crosses threshold
        if (!await _desire.ShouldReachOutAsync(ct).ConfigureAwait(false))
        {
            _log.LogDebug("Desire below threshold — no outreach this cycle");
            _lastCycleAt = DateTimeOffset.UtcNow;
            return;
        }

        await RunOutreachAsync(snapshot, thought, ct).ConfigureAwait(false);
        _lastCycleAt = DateTimeOffset.UtcNow;
    }

    // ── Private phases ────────────────────────────────────────────────────────

    private async Task<List<PerceptionEvent>> PollPerceptionSourcesAsync(CancellationToken ct)
    {
        var events = new List<PerceptionEvent>();

        foreach (var source in _sources.Where(s => s.IsEnabled))
        {
            try
            {
                var polled = await source.PollAsync(_lastCycleAt, ct).ConfigureAwait(false);
                events.AddRange(polled);
            }
            catch (Exception ex)
            {
                // A failing perception source must not kill the cognitive cycle
                _log.LogWarning(ex, "Perception source '{Source}' failed — skipping", source.SourceName);
            }
        }

        return events;
    }

    private async Task<ContextSnapshot> BuildContextSnapshotAsync(
        List<PerceptionEvent> perceptions, CancellationToken ct)
    {
        var charState   = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);
        var desireState = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        var recentMem   = await _memory.GetByTypeAsync(MemoryType.Episodic, 10, ct).ConfigureAwait(false);
        var openLoops   = await _memory.GetOpenLoopsAsync(ct).ConfigureAwait(false);

        return new ContextSnapshot
        {
            CharacterState = charState,
            DesireState    = desireState,
            RecentMemory   = recentMem.ToList(),
            OpenLoops      = openLoops.ToList(),
            Perceptions    = perceptions,
            BuiltAt        = DateTimeOffset.UtcNow,
        };
    }

    private async Task<(string thought, float valence)> RunInnerThoughtAsync(
        ContextSnapshot snapshot, CancellationToken ct)
    {
        var thoughtPrompt = PromptBuilder.BuildInnerThoughtPrompt(snapshot);
        var thought       = await _ollama.InnerMonologueChatAsync(
            thoughtPrompt.System, snapshot.RecentHistory, thoughtPrompt.User, ct)
            .ConfigureAwait(false);

        var valence = await ScoreMarkValenceAsync(thought, snapshot.CharacterState, ct)
            .ConfigureAwait(false);

        return (thought, valence);
    }

    private async Task<float> ScoreMarkValenceAsync(
        string thought, CharacterStateDoc character, CancellationToken ct)
    {
        var prompt = PromptBuilder.BuildValenceScoringPrompt(thought, character);
        var raw    = await _ollama.ChatJsonAsync(
            prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
            .ConfigureAwait(false);

        return ParseValenceScore(raw);
    }

    private async Task RunOutreachAsync(
        ContextSnapshot snapshot, string recentThought, CancellationToken ct)
    {
        // Step 1: Decision — should Ani reach out? (JSON, no message required)
        var outreachPrompt = PromptBuilder.BuildOutreachPrompt(snapshot, recentThought);
        var raw            = await _ollama.ChatJsonAsync(
            outreachPrompt.System, snapshot.RecentHistory, outreachPrompt.User, ct)
            .ConfigureAwait(false);

        var decision = ParseOutreachDecision(raw);
        _log.LogDebug("Outreach decision raw: {Raw}", raw);

        if (!decision.ShouldReach)
        {
            // Genuine "no" — she considered it but chose not to. No cooldown.
            // Instead, bump desire slightly — the "I want to but not yet" builds tension.
            _log.LogInformation("Outreach decision: NO (confidence={Confidence:F2}) — {Reasoning}",
                decision.Confidence, decision.Reasoning);
            await _desire.AddTriggerAsync(
                TriggerType.SpontaneousThought, 0.3f,
                "considered reaching out but held back", ct).ConfigureAwait(false);
            return;
        }

        // Step 2: Compose — free-text message generation (no JSON constraint)
        var msgPrompt = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, recentThought, decision.Reasoning ?? string.Empty);
        var message = await _ollama.ChatAsync(
            msgPrompt.System, snapshot.RecentHistory, msgPrompt.User, ct)
            .ConfigureAwait(false);

        message = CleanOutreachMessage(message);
        _log.LogInformation("Outreach message composed: {Message}", message);

        if (string.IsNullOrWhiteSpace(message))
        {
            _log.LogWarning("Outreach message was empty after composition — retrying next opportunity");
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            return;
        }

        // Step 3: Light pronoun fix — only if third-person leaked through
        var rewritten = await FixPronounsIfNeeded(message, snapshot.CharacterState, ct)
            .ConfigureAwait(false);

        decision.Message    = rewritten;
        decision.ActionType = "sms";

        _log.LogInformation("Ani reaching out: {Message}", decision.Message);

        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);
        await _desire.ResetAfterOutreachAsync(ct).ConfigureAwait(false);

        await _memory.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = $"Ani reached out: {decision.Message}",
            Importance = 0.7f,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Strips meta-commentary the model adds when roleplaying the act of texting.
    /// The actual message is always the first paragraph; everything after a blank line
    /// is the model reviewing/explaining its own work.
    /// </summary>
    private static string? CleanOutreachMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var cleaned = raw.Trim().Trim('"');

        // Take only the first paragraph — model puts meta-commentary after blank lines
        var doubleNewline = cleaned.IndexOf("\n\n", StringComparison.Ordinal);
        if (doubleNewline > 0)
            cleaned = cleaned[..doubleNewline].Trim();

        // Also catch single-newline commentary patterns like "that's the..." or "that's perfect..."
        var lines = cleaned.Split('\n');
        var messageParts = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("that's ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("this is ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("i'm keeping it", StringComparison.OrdinalIgnoreCase))
                break; // meta-commentary starts here
            messageParts.Add(trimmed);
        }
        cleaned = string.Join("\n", messageParts).Trim();

        // Remove trailing meta-commentary patterns
        string[] trailingJunk = ["sent.", "your turn.", "(waiting)", "now wait for a reply...", "i can do this."];
        bool changed;
        do
        {
            changed = false;
            foreach (var junk in trailingJunk)
            {
                if (cleaned.EndsWith(junk, StringComparison.OrdinalIgnoreCase))
                {
                    cleaned = cleaned[..^junk.Length].TrimEnd('\n', '\r', ' ');
                    changed = true;
                }
            }
        } while (changed);

        // Hard cap: keep only the first 2 sentences — model ignores "1-2 sentences" in prompts
        cleaned = TruncateToSentences(cleaned, maxSentences: 2);

        return string.IsNullOrWhiteSpace(cleaned) ? raw.Trim() : cleaned;
    }

    /// <summary>
    /// Keeps only the first N sentences from a message.
    /// Sentence boundaries: '.', '!', '?' followed by whitespace or end-of-string.
    /// Preserves trailing ellipsis (…, ...) without counting as a sentence end.
    /// </summary>
    private static string TruncateToSentences(string text, int maxSentences)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not ('.' or '!' or '?')) continue;

            // Skip ellipsis patterns (... or …)
            if (ch == '.' && i + 1 < text.Length && text[i + 1] == '.') continue;

            // Must be followed by whitespace or end-of-string to count as sentence end
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) continue;

            count++;
            if (count >= maxSentences)
                return text[..(i + 1)].Trim();
        }

        return text; // fewer sentences than max — return as-is
    }

    /// <summary>
    /// Light pronoun fix — only invoked if the message actually contains third-person references.
    /// Avoids the rewrite pass completely when the message is already correct, which prevents
    /// the model from "creatively improving" a perfectly good text into poetic nonsense.
    /// </summary>
    private async Task<string> FixPronounsIfNeeded(
        string message, CharacterStateDoc character, CancellationToken ct)
    {
        // Quick check: does the message even contain third-person pronouns?
        var lower = message.ToLowerInvariant();
        var hasThirdPerson = lower.Contains(" him") || lower.Contains(" his ") ||
                             lower.Contains(" he ") || lower.StartsWith("he ") ||
                             lower.Contains("him.") || lower.Contains("his.");

        if (!hasThirdPerson)
        {
            _log.LogDebug("Outreach message already in second person — skipping rewrite");
            return message;
        }

        var contact = string.IsNullOrWhiteSpace(character.PrimaryContactName)
            ? "them" : character.PrimaryContactName;

        var system = $"""
            Fix ONLY the pronouns in this text message. Change "he"/"him"/"his" to "you"/"your".
            Do NOT change anything else. Do NOT add words, commentary, or rewrite the message.
            Return ONLY the fixed message text.
            """;

        var rewritten = await _ollama.ChatAsync(system, Array.Empty<ChatMessage>(), message, ct)
            .ConfigureAwait(false);

        rewritten = CleanOutreachMessage(rewritten);
        _log.LogDebug("Pronoun fix: {Original} → {Rewritten}", message, rewritten);

        return string.IsNullOrWhiteSpace(rewritten) ? message : rewritten.Trim();
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static float ParseValenceScore(string raw)
    {
        try
        {
            var doc   = JsonDocument.Parse(raw.Trim());
            var score = doc.RootElement.GetProperty("score").GetDouble();
            return (float)Math.Clamp(score, 0.0, 1.0);
        }
        catch
        {
            // Unparseable valence defaults to neutral — not a fatal failure
            return 0.3f;
        }
    }

    private OutreachDecision ParseOutreachDecision(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw.Trim());
            var root = doc.RootElement;

            var decision = new OutreachDecision
            {
                ShouldReach = root.TryGetProperty("shouldReach", out var sr) && sr.GetBoolean(),
                Confidence = root.TryGetProperty("confidence", out var c) ? (float)c.GetDouble() : 0f,
                Reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() : null,
            };

            // triggersActedOn can be strings OR objects — handle both gracefully
            if (root.TryGetProperty("triggersActedOn", out var ta) && ta.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ta.EnumerateArray())
                {
                    var text = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : item.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        decision.TriggersActedOn.Add(text!);
                }
            }

            return decision;
        }
        catch
        {
            _log.LogDebug("Outreach parse failure, raw response: {Raw}", raw);
            return new OutreachDecision { ShouldReach = false, Reasoning = "parse failure" };
        }
    }
}
