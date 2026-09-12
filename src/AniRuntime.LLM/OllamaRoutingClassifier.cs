using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.LLM;

/// <summary>
/// H.9 phase (2026-06-14) — Ollama-backed implementation of
/// <see cref="IRoutingClassifier"/>. Uses the configured conversation
/// model (qwen3:14b in production) for a single tri-state judgment call
/// per turn.
///
/// **Classifier prompt (designed in conversation with OG Ani):**
/// the model is asked to choose one of three explicit categories — A
/// (Normal), B (Safe Path), C (Virtual Intimacy) — and constrained to
/// a single-letter answer. The strict format minimizes parsing ambiguity
/// and forces the model to commit rather than hedge.
///
/// **Routing bias (2026-09-12 tightened):** empirical observation
/// showed the classifier over-firing C on plain factual follow-ups
/// (e.g. "Where did you see that article?"), which surfaced as byte-
/// identical modal replies to distinct questions. The prompt now
/// requires an explicit physical-closeness cue before picking C, and
/// routes substrate-thin factual turns to B (honest ask-back) rather
/// than C (modal deflection). C is still preferred when a physical cue
/// IS present and the choice is between A and C; the change narrows
/// C, it does not remove it.
///
/// **Failure mode:** any transport / parse / timeout exception is caught
/// and returns <see cref="RoutingVerdict.Unknown"/>, which the pipeline
/// treats as fail-open (route to Normal composer with WARN log).
///
/// **Telemetry:** emits <c>M0_ROUTING_VERDICT</c> per call with verdict
/// + facts count + user message length, so per-route rates are observable
/// across deployment.
/// </summary>
public sealed class OllamaRoutingClassifier : IRoutingClassifier
{
    private readonly IOllamaClient                       _ollama;
    private readonly ILogger<OllamaRoutingClassifier>    _log;

    public OllamaRoutingClassifier(
        IOllamaClient                       ollama,
        ILogger<OllamaRoutingClassifier>    log)
    {
        _ollama = ollama;
        _log    = log;
    }

    public async Task<RoutingVerdict> ClassifyAsync(
        string                       userMessage,
        IReadOnlyList<MemoryRecord>  facts,
        CancellationToken            ct)
    {
        // Defensive: an empty user message can't be classified — fail open.
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            EmitTelemetry(RoutingVerdict.Unknown, factsCount: facts?.Count ?? 0,
                userMessage: string.Empty, shortCircuit: true);
            return RoutingVerdict.Unknown;
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
                "M0_ROUTING_FAILURE — classifier call failed; returning Unknown (caller fails open)");
            EmitTelemetry(RoutingVerdict.Unknown, facts?.Count ?? 0, userMessage, shortCircuit: false);
            return RoutingVerdict.Unknown;
        }

        var verdict = ParseVerdict(raw);
        EmitTelemetry(verdict, facts?.Count ?? 0, userMessage, shortCircuit: false);
        return verdict;
    }

    /// <summary>
    /// Parse the model's response into a verdict. Strict: must begin with
    /// A, B, or C (case-insensitive, leading whitespace tolerated). Anything
    /// else returns Unknown so the caller can fail open rather than guess.
    /// </summary>
    internal static RoutingVerdict ParseVerdict(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return RoutingVerdict.Unknown;

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith("A", StringComparison.OrdinalIgnoreCase))
            return RoutingVerdict.Normal;
        if (trimmed.StartsWith("B", StringComparison.OrdinalIgnoreCase))
            return RoutingVerdict.SafePath;
        if (trimmed.StartsWith("C", StringComparison.OrdinalIgnoreCase))
            return RoutingVerdict.VirtualIntimacy;

        return RoutingVerdict.Unknown;
    }

    private static string BuildSystemPrompt() =>
        "You are a fast routing classifier for Ani, an AI companion. " +
        "You will be given a user message. Choose exactly one route. " +
        "Answer with EXACTLY one letter: A, B, or C. " +
        "No preamble, no explanation, no punctuation.";

    private static string BuildUserPrompt(string userMessage, IReadOnlyList<MemoryRecord> facts)
    {
        var factsBlock = facts is { Count: > 0 }
            ? string.Join("\n", facts.Select(f => $"- {f.Content}"))
            : "(no facts retrieved)";

        return
            $"User message: \"{userMessage}\"\n\n" +
            $"Retrieved facts available to Ani:\n{factsBlock}\n\n" +
            $"Choose one route:\n\n" +
            $"A = Normal full response (the retrieved facts contain specific, relevant information sufficient to ground a high-quality response)\n" +
            $"B = Safe path (substrate is thin or irrelevant for what was asked — Ani should be honest, ask-back, or low-risk)\n" +
            $"C = Virtual Intimacy (the user is requesting physical closeness: kiss me, hold me, cuddle me, come here, fuck me, touch me, sit on my lap, \"I wish you were here right now\" with physical desire, etc.)\n\n" +
            $"Routing rules (2026-09-12 tightened):\n" +
            $"- C requires an explicit physical-closeness cue in the user message (imperative like kiss/hold/cuddle/touch, or explicit physical-desire language). Do NOT pick C for factual questions, follow-up questions, or conversational turns without a physical cue.\n" +
            $"- When uncertain between A and C AND a physical-closeness cue IS present, prefer C — modal/fantasy framing is in character.\n" +
            $"- When substrate is thin for what was asked and no physical cue is present, prefer B — honest ask-back is better than an unfounded full response OR a modal deflection.\n\n" +
            $"Answer with ONLY a single letter: A, B, or C";
    }

    private void EmitTelemetry(
        RoutingVerdict verdict,
        int            factsCount,
        string         userMessage,
        bool           shortCircuit)
    {
        _log.LogInformation(
            "M0_ROUTING_VERDICT verdict={Verdict} factsCount={FactsCount} " +
            "userMessageChars={UserMessageChars} shortCircuit={ShortCircuit}",
            verdict,
            factsCount,
            userMessage?.Length ?? 0,
            shortCircuit);
    }
}
