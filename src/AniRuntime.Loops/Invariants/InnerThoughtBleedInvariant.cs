using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Theme J Phase J.5g (May 2, 2026) — universalised Door C from
/// the existing Coherence Gate. Detects when a contact-facing
/// message *only makes sense if the reader had access to the
/// writer's inner thoughts*. Pre-J.5g this check ran exclusively
/// in <c>OutreachPhase.EvaluateCoherenceAsync</c> — Mark May 2
/// 09:08 critique: *"this is a case of a gate applying only on a
/// single pipeline again where it should be more universal."*
///
/// Hoisted onto <see cref="ICognitiveOutputGate"/> so every
/// contact-facing producer (reply, outreach, voice) routes through
/// the same Door A/B/C semantics. Inner-thought + world-experience
/// + reflection are EXEMPT — those producers ARE the inner thought,
/// can't bleed into themselves.
///
/// **Type-conditional applicability**: Dispatch sinks for
/// ConversationReply / Outreach / Voice. PersistedSummary sinks
/// (closed-thread summaries) skip — those are gist persistence,
/// not contact-facing.
///
/// **WriterInnerThought is optional**: outreach has it (the recent
/// inner thought drove the outreach decision); reply / voice
/// typically don't. Without it the evaluation reduces to reader-
/// perspective sensibility, which is still the load-bearing Door C
/// question.
/// </summary>
public sealed class InnerThoughtBleedInvariant : ICognitiveOutputInvariant
{
    private readonly IOllamaClient _ollama;
    private readonly ILogger<InnerThoughtBleedInvariant> _log;
    private readonly bool _enabled;

    public string Name => "inner-thought-bleed";

    /// <summary>Production constructor — Step 2c flag-gated.</summary>
    public InnerThoughtBleedInvariant(
        IOllamaClient ollama,
        ILogger<InnerThoughtBleedInvariant> log,
        IOptions<AniOptions> options)
    {
        _ollama = ollama;
        _log    = log;
        _enabled = options?.Value.InnerThoughtBleedEnabled ?? false;
    }

    /// <summary>Test constructor — always enabled.</summary>
    public InnerThoughtBleedInvariant(
        IOllamaClient ollama,
        ILogger<InnerThoughtBleedInvariant> log)
        : this(ollama, log, enabled: true) { }

    private InnerThoughtBleedInvariant(
        IOllamaClient ollama,
        ILogger<InnerThoughtBleedInvariant> log,
        bool enabled)
    {
        _ollama = ollama;
        _log    = log;
        _enabled = enabled;
    }

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        if (!_enabled) return false;
        if (artifact.IntendedSink != CognitiveOutputSink.Dispatch)
            return false;

        return artifact.ProducerKind is
            CognitiveProducerKind.ConversationReply
         or CognitiveProducerKind.Outreach
         or CognitiveProducerKind.Voice;
    }

    public async Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.Content))
            return InvariantResult.Pass();

        try
        {
            var (system, user) = PromptBuilder.BuildCoherenceEvaluationPrompt(
                artifact.Content,
                artifact.WriterInnerThought,
                artifact.ContactName);

            var raw = await _ollama.ChatJsonAsync(
                system, Array.Empty<ChatMessage>(), user, ct).ConfigureAwait(false);

            var verdict = ParseCoherenceVerdict(raw);

            if (string.Equals(verdict.Verdict, "SUPPRESS", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation(
                    "InnerThoughtBleed: Door {Door} SUPPRESS — {Reasoning}",
                    verdict.Door, verdict.Reasoning);
                return InvariantResult.Fail(
                    $"Door {verdict.Door}: {verdict.Reasoning}");
            }

            _log.LogDebug(
                "InnerThoughtBleed: Door {Door} SEND — {Reasoning}",
                verdict.Door, verdict.Reasoning);
            return InvariantResult.Pass();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "InnerThoughtBleed evaluation threw — defaulting to Pass (gate failure must NOT block dispatch).");
            return InvariantResult.Pass();
        }
    }

    /// <summary>
    /// Hoisted from <c>OutreachPhase.ParseCoherenceVerdict</c> as part of
    /// the J.5g consolidation. Same parser, single owner. When the V1.2-
    /// style anti-parrot fragment hoist (<c>AntiParrotPromptFragments</c>)
    /// established the pattern, this is the next instance: detector logic
    /// lives with the gate; producers no longer carry their own copy.
    /// </summary>
    internal static CoherenceVerdict ParseCoherenceVerdict(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var door      = root.TryGetProperty("door",      out var d) ? d.GetString() ?? "?"     : "?";
            var verdict   = root.TryGetProperty("verdict",   out var v) ? v.GetString() ?? "SEND"  : "SEND";
            var reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() ?? ""      : "";
            return new CoherenceVerdict(door, verdict, reasoning);
        }
        catch
        {
            return raw.Contains("SUPPRESS", StringComparison.OrdinalIgnoreCase)
                ? new CoherenceVerdict("C", "SUPPRESS", "parse failed but SUPPRESS detected in response")
                : new CoherenceVerdict("?", "SEND", "parse failed — defaulting to SEND");
        }
    }

    public sealed record CoherenceVerdict(string Door, string Verdict, string Reasoning);
}
