using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.LLM;

/// <summary>
/// H phase (2026-06-12) — Ollama-backed implementation of
/// <see cref="IRelevanceFilter"/>. Uses the configured conversation model
/// (qwen3:14b in production) for a single binary judgment call per turn.
///
/// **Filter prompt (anchored to the 2026-06-11 puzzle-turn test):**
/// the model is asked one question — *"does the retrieved context contain
/// enough specific, relevant information to give a high-quality, grounded
/// response?"* — and constrained to a one-word answer (YES or NO). The
/// strict format minimizes parsing ambiguity and reduces the model's
/// opportunity to elaborate (which is where confab leaks into judgment).
///
/// **Failure mode:** any transport / parse / timeout exception is caught
/// and returns <see cref="RelevanceVerdict.Unknown"/>, which the pipeline
/// treats as fail-open (route to normal composer with WARN log). This
/// preserves UX continuity at the cost of one defense-in-depth layer.
///
/// **Telemetry:** emits <c>M0_RELEVANCE_FILTER_VERDICT</c> per call with
/// verdict + facts count + user message length, so the substrate-thinness
/// rate is observable across deployment.
/// </summary>
public sealed class OllamaRelevanceFilter : IRelevanceFilter
{
    private readonly IOllamaClient                       _ollama;
    private readonly ILogger<OllamaRelevanceFilter>      _log;

    public OllamaRelevanceFilter(
        IOllamaClient                       ollama,
        ILogger<OllamaRelevanceFilter>      log)
    {
        _ollama = ollama;
        _log    = log;
    }

    public async Task<RelevanceVerdict> JudgeAsync(
        string                       userMessage,
        IReadOnlyList<MemoryRecord>  facts,
        CancellationToken            ct)
    {
        // Defensive: with no facts at all, the answer is structurally NO
        // — no point spending a model call on a question with a known answer.
        if (facts is null || facts.Count == 0)
        {
            EmitTelemetry(RelevanceVerdict.No, factsCount: 0, userMessage, shortCircuit: true);
            return RelevanceVerdict.No;
        }

        var system = BuildSystemPrompt();
        var user   = BuildUserPrompt(userMessage, facts);

        string raw;
        try
        {
            raw = await _ollama.ChatAsync(
                systemPrompt: system,
                history:      Array.Empty<ChatMessage>(),
                userMessage:  user,
                ct:           ct,
                temperature:  0.0f)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "M0_RELEVANCE_FILTER_FAILURE — judgment call failed; returning Unknown (caller fails open)");
            EmitTelemetry(RelevanceVerdict.Unknown, facts.Count, userMessage, shortCircuit: false);
            return RelevanceVerdict.Unknown;
        }

        var verdict = ParseVerdict(raw);
        EmitTelemetry(verdict, facts.Count, userMessage, shortCircuit: false);
        return verdict;
    }

    /// <summary>
    /// Parse the model's response into a verdict. Strict: must begin with
    /// YES or NO (case-insensitive, leading whitespace tolerated). Anything
    /// else returns Unknown so the caller can fail open rather than guess.
    /// </summary>
    internal static RelevanceVerdict ParseVerdict(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return RelevanceVerdict.Unknown;

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith("YES", StringComparison.OrdinalIgnoreCase))
            return RelevanceVerdict.Yes;
        if (trimmed.StartsWith("NO",  StringComparison.OrdinalIgnoreCase))
            return RelevanceVerdict.No;

        return RelevanceVerdict.Unknown;
    }

    private static string BuildSystemPrompt() =>
        "You are a strict relevance filter. " +
        "You will be given a user message and a list of retrieved facts. " +
        "Answer with EXACTLY one word: YES or NO. " +
        "No preamble, no explanation, no punctuation. " +
        "Answer YES only when the retrieved facts contain specific, relevant information " +
        "sufficient to ground a high-quality response to the user's message. " +
        "Answer NO when the facts are vague, off-topic, or insufficient.";

    private static string BuildUserPrompt(string userMessage, IReadOnlyList<MemoryRecord> facts)
    {
        var factsBlock = string.Join("\n",
            facts.Select(f => $"- {f.Content}"));

        return
            $"User message: \"{userMessage}\"\n\n" +
            $"Retrieved facts:\n{factsBlock}\n\n" +
            "Does the retrieved context contain enough specific, relevant information to give a high-quality, grounded response?\n\n" +
            "Answer with ONLY: YES or NO";
    }

    private void EmitTelemetry(
        RelevanceVerdict verdict,
        int              factsCount,
        string           userMessage,
        bool             shortCircuit)
    {
        _log.LogInformation(
            "M0_RELEVANCE_FILTER_VERDICT verdict={Verdict} factsCount={FactsCount} " +
            "userMessageChars={UserMessageChars} shortCircuit={ShortCircuit}",
            verdict,
            factsCount,
            userMessage?.Length ?? 0,
            shortCircuit);
    }
}
