using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Generates inner thoughts, reflections, and valence scores.
///
/// Extracted from CognitiveCycleProcessor (SRP) — inner thought generation
/// is a distinct responsibility from cycle orchestration. This class owns
/// the LLM calls for private monologue, reflection, and valence scoring.
///
/// **Theme J Phase J.5h-prelude (May 3, 2026) — gate wiring.** Inner-thought
/// outputs route through <see cref="ICognitiveOutputGate"/> at the
/// generation boundary BEFORE returning to the cycle. The May 3 10:55
/// "perez" failure traced to an inner-thought-side fabrication that was
/// saved as Interior-tier substrate and then lifted into outreach
/// composition. Gating the thought at production catches the substrate
/// laundering at its source. On Remediate/Fail verdict, the thought is
/// dropped (returned as empty string) — inner thoughts are not user-
/// facing, dropping is safe; substrate doesn't accumulate the suspect
/// content; the next cycle generates fresh.
/// </summary>
public class InnerThoughtPhase
{
    private readonly IOllamaClient _ollama;
    private readonly ILogger<InnerThoughtPhase> _log;
    private readonly ICognitiveOutputGate? _outputGate;

    public InnerThoughtPhase(
        IOllamaClient ollama,
        ILogger<InnerThoughtPhase> log,
        ICognitiveOutputGate? outputGate = null)
    {
        _ollama = ollama;
        _log = log;
        _outputGate = outputGate;
    }

    /// <summary>
    /// Generates an inner thought, scores its relational valence, and optionally
    /// produces a reflection (Park et al. generative agent reflection layer).
    /// </summary>
    public async Task<(string Thought, string? Reflection, float Valence)> RunAsync(
        ContextSnapshot snapshot, CancellationToken ct)
    {
        var thoughtPrompt = PromptBuilder.BuildInnerThoughtPrompt(snapshot);
        var thought = await _ollama.InnerMonologueChatAsync(
            thoughtPrompt.System, snapshot.RecentHistory, thoughtPrompt.User, ct)
            .ConfigureAwait(false);

        // Theme J Phase J.5h-prelude (May 3, 2026) — gate the thought before it
        // becomes substrate. SelfEchoInvariant catches inner-thought-loops
        // (duck-norris / dinner-at-seven / vanilla-cream-soda class); other
        // applicable invariants (PromptTemplateLeak, Confabulation, temporal
        // sub-claims) catch additional classes via type-conditional dispatch.
        if (!string.IsNullOrWhiteSpace(thought))
        {
            thought = await GateThoughtAsync(thought, snapshot, thoughtPrompt.System, ct)
                .ConfigureAwait(false);
        }

        // Score the raw thought for valence BEFORE reflection. Skip when the
        // gate dropped the thought — there's nothing to score.
        var valence = string.IsNullOrWhiteSpace(thought)
            ? 0.3f
            : await ScoreRelationalValenceAsync(thought, snapshot.CharacterState, ct)
                .ConfigureAwait(false);

        // Reflection layer (Park et al.) — only run if thought survived the gate
        var reflection = string.IsNullOrWhiteSpace(thought)
            ? null
            : await ReflectOnThoughtAsync(thought, snapshot, ct).ConfigureAwait(false);

        return (thought, reflection, valence);
    }

    /// <summary>
    /// Theme J Phase J.5h-prelude (May 3, 2026) — route the produced thought
    /// through the universal cognitive-output gate. Drop-on-fail semantics:
    /// inner thoughts that trip the gate become empty strings rather than
    /// polluting Interior-tier substrate. The gate's
    /// <see cref="ICognitiveOutputInvariant.AppliesTo"/> filtering ensures
    /// only inner-thought-applicable invariants run (e.g. self-echo,
    /// prompt-template-leak; not anti-parrot which is contact-facing).
    /// Gate exceptions are caught and logged; the thought passes through
    /// uncovered (gate observability bugs MUST NOT block the cognitive
    /// cycle from producing thoughts).
    /// </summary>
    internal async Task<string> GateThoughtAsync(
        string thought, ContextSnapshot snapshot, string systemPromptText, CancellationToken ct)
    {
        if (_outputGate is null) return thought;

        // Recent inner thoughts feed SelfEchoInvariant — looking for
        // verbatim self-templating across cycles (the duck-norris loop class).
        var priorThoughts = snapshot.RelevantMemory?
            .Where(m => m.Type == MemoryType.InnerThought)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Take(8)
            .ToList();

        var artifact = new CognitiveArtifact
        {
            Content                 = thought,
            ProducerKind            = CognitiveProducerKind.InnerThought,
            IntendedSink            = CognitiveOutputSink.PersistedMemory,
            ContactName             = snapshot.CharacterState?.PrimaryContactName ?? Roles.Mark,
            GeneratedAt             = DateTimeOffset.UtcNow,
            PriorAniMessages        = priorThoughts,
            SystemPromptText        = systemPromptText,
        };

        OutputGateResult result;
        try
        {
            result = await _outputGate.EvaluateAsync(artifact, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "J.5h inner-thought gate threw — passing thought uncovered (gate failure must NOT block cognitive cycle).");
            return thought;
        }

        if (result.Verdict == OutputGateVerdict.Pass) return thought;

        _log.LogWarning(
            "J.5h inner-thought gate {Verdict} [{Fired}] — dropping thought from substrate. Hint: {Hint}",
            result.Verdict, string.Join(",", result.FiredInvariants), result.RemediationHint);
        return string.Empty;
    }

    private async Task<string?> ReflectOnThoughtAsync(
        string thought, ContextSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var reflectionPrompt = PromptBuilder.BuildReflectionPrompt(thought, snapshot);
            var reflection = await _ollama.InnerMonologueChatAsync(
                reflectionPrompt.System, Array.Empty<ChatMessage>(), reflectionPrompt.User, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(reflection))
                return null;

            reflection = reflection.Trim();
            if (reflection.Length > 200)
                reflection = reflection[..200];

            _log.LogDebug("Reflection: {Reflection}", reflection);
            return reflection;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reflection failed — continuing without");
            return null;
        }
    }

    private async Task<float> ScoreRelationalValenceAsync(
        string thought, CharacterStateDoc character, CancellationToken ct)
    {
        var prompt = PromptBuilder.BuildValenceScoringPrompt(thought, character);
        var raw = await _ollama.ChatJsonAsync(
            prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
            .ConfigureAwait(false);

        return ParseValenceScore(raw);
    }

    internal static float ParseValenceScore(string raw)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(raw.Trim());
            var score = doc.RootElement.GetProperty("score").GetDouble();
            return (float)Math.Clamp(score, 0.0, 1.0);
        }
        catch
        {
            return 0.3f;
        }
    }
}
