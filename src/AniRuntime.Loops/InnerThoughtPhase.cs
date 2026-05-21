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
/// **Posture-S+1 (Issue #38, 2026-05-17) — hybrid two-call cycle gated on
/// <see cref="AniOptions.UseHybridInnerThoughtCycle"/>.** When enabled,
/// the cycle replaces the three-call legacy path
/// (v7-thought + v7-self-valence + v7-reflection-on-thought) with a
/// two-call hybrid: <c>ani-v7-inner</c> produces the thought (preserving
/// voice + caregiver-pivot resistance from training);
/// <c>qwen3:14b</c> reads the thought + context and emits structured
/// metadata (register, valence, importance, associative-anchor) framed as
/// recognizer, not external rater. Reflection-as-second-call is dropped —
/// Mark's empirical observation that the two-part structure trains the
/// duck-norris / vanilla-cream-soda recurrence loops. Default OFF until
/// production soak validates the same shape the May 17 evening probe
/// demonstrated. See <c>docs/spec/ANI-Substrate-Led-Character-Plan.md</c>
/// §7 and Paper 3 Contribution 8.
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
    private readonly IEpistemicSubstrateRenderer? _epistemicRenderer;
    private readonly bool _epistemicFramingEnabled;
    private readonly bool _hybridCycleEnabled;
    private readonly string _hybridMetadataModel;

    public InnerThoughtPhase(
        IOllamaClient ollama,
        ILogger<InnerThoughtPhase> log,
        ICognitiveOutputGate? outputGate = null,
        Microsoft.Extensions.Options.IOptions<AniOptions>? aniOptions = null,
        IEpistemicSubstrateRenderer? epistemicRenderer = null)
    {
        _ollama = ollama;
        _log = log;
        _outputGate = outputGate;
        _epistemicRenderer = epistemicRenderer;
        _epistemicFramingEnabled = aniOptions?.Value.EpistemicFramingEnabled ?? false;
        _hybridCycleEnabled = aniOptions?.Value.UseHybridInnerThoughtCycle ?? false;
        _hybridMetadataModel = aniOptions?.Value.HybridInnerThoughtMetadataModel ?? "qwen3:14b";
    }

    /// <summary>
    /// Generates an inner thought, scores its relational valence, and optionally
    /// produces a reflection (Park et al. generative agent reflection layer).
    ///
    /// Posture-S+1: when <see cref="AniOptions.UseHybridInnerThoughtCycle"/> is
    /// true, the metadata fields (Register, Importance, AssociativeAnchor) on
    /// the returned <see cref="InnerThoughtResult"/> are populated by the
    /// hybrid metadata-recognizer call; the consumer uses them directly and
    /// skips the legacy threshold/extraction logic. When false, those fields
    /// are null and the consumer applies the legacy external-judge path.
    /// </summary>
    public async Task<InnerThoughtResult> RunAsync(
        ContextSnapshot snapshot, CancellationToken ct)
    {
        var rendererForPrompt = _epistemicFramingEnabled ? _epistemicRenderer : null;
        var thoughtPrompt = PromptBuilder.BuildInnerThoughtPrompt(snapshot, rendererForPrompt);
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

        if (string.IsNullOrWhiteSpace(thought))
        {
            // Gate dropped or empty — nothing else to do.
            return new InnerThoughtResult(thought, Reflection: null, Valence: 0.3f);
        }

        // Posture-S+1 hybrid path. Single Qwen call recognizes register,
        // valence, importance, and associative-anchor from the thought v7
        // already produced. Drops the separate reflection call (May 17
        // empirical finding: two-part structure trains continual loops).
        if (_hybridCycleEnabled)
        {
            var metadata = await RecognizeMetadataAsync(thought, snapshot, ct)
                .ConfigureAwait(false);
            return new InnerThoughtResult(
                Thought:           thought,
                Reflection:        null,
                Valence:           metadata.Valence,
                Register:          metadata.Register,
                Importance:        metadata.Importance,
                AssociativeAnchor: metadata.AssociativeAnchor);
        }

        // Legacy three-call path. Kept callable for safe rollout and so
        // empirical comparison data remains collectable.
        var valence = await ScoreRelationalValenceAsync(thought, snapshot.CharacterState, ct)
            .ConfigureAwait(false);
        var reflection = await ReflectOnThoughtAsync(thought, snapshot, ct).ConfigureAwait(false);

        return new InnerThoughtResult(thought, reflection, valence);
    }

    /// <summary>
    /// Posture-S+1 — runs the hybrid metadata-recognizer call against the
    /// configured local model (default <c>qwen3:14b</c>). Failure-tolerant:
    /// if the call fails or returns malformed JSON, falls back to the
    /// legacy self-valence score so the cycle never blocks. The fallback
    /// path is logged so production telemetry can distinguish "hybrid
    /// landed" from "hybrid fell through."
    /// </summary>
    internal async Task<MetadataRecognitionResult> RecognizeMetadataAsync(
        string thought, ContextSnapshot snapshot, CancellationToken ct)
    {
        var (system, user) = PromptBuilder.BuildInnerThoughtMetadataPrompt(thought, snapshot);

        string raw;
        try
        {
            raw = await _ollama.ChatJsonWithModelAsync(
                _hybridMetadataModel, system, Array.Empty<ChatMessage>(), user, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Posture-S+1 hybrid metadata recognizer failed ({Model}); falling back to legacy valence-scoring.",
                _hybridMetadataModel);
            return await FallbackToLegacyMetadataAsync(thought, snapshot, ct).ConfigureAwait(false);
        }

        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(raw.Trim());
            var root = doc.RootElement;

            var register   = root.TryGetProperty("register", out var r) ? r.GetString() : null;
            var valence    = root.TryGetProperty("valence", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                                ? (float)Math.Clamp(v.GetDouble(), 0.0, 1.0) : 0.3f;
            var importance = root.TryGetProperty("importance", out var i) && i.ValueKind == System.Text.Json.JsonValueKind.Number
                                ? (float)Math.Clamp(i.GetDouble(), 0.0, 1.0) : 0.5f;
            string? anchor = null;
            if (root.TryGetProperty("associative_anchor", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var aStr = a.GetString();
                if (!string.IsNullOrWhiteSpace(aStr)) anchor = aStr;
            }

            if (string.IsNullOrWhiteSpace(register))
            {
                _log.LogWarning(
                    "Posture-S+1 hybrid metadata produced empty register field; falling back to legacy valence-scoring.");
                return await FallbackToLegacyMetadataAsync(thought, snapshot, ct).ConfigureAwait(false);
            }

            return new MetadataRecognitionResult(register, valence, importance, anchor);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Posture-S+1 hybrid metadata recognizer returned malformed JSON; falling back. Raw: {Raw}",
                raw.Length > 300 ? raw[..300] : raw);
            return await FallbackToLegacyMetadataAsync(thought, snapshot, ct).ConfigureAwait(false);
        }
    }

    private async Task<MetadataRecognitionResult> FallbackToLegacyMetadataAsync(
        string thought, ContextSnapshot snapshot, CancellationToken ct)
    {
        var valence = await ScoreRelationalValenceAsync(thought, snapshot.CharacterState, ct)
            .ConfigureAwait(false);
        // Legacy importance derivation lives in CognitiveCycleProcessor as a
        // valence-threshold; we don't have AniOptions here to compute the same
        // threshold, so emit a single representative value matched to the legacy
        // post-threshold midpoint. Register/anchor null signals "no recognizer
        // signal" to the consumer.
        return new MetadataRecognitionResult(
            Register:          "Wistful",
            Valence:           valence,
            Importance:        valence >= 0.5f ? 0.8f : 0.3f,
            AssociativeAnchor: null);
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

        // Theme J Phase J.5h (Issue #47, 2026-05-21) — feed
        // SourceAttributionInvariant the contact-side signal. Populated
        // explicitly (empty list when no Mark turns recently, not null) so
        // the invariant can distinguish "no Mark turns to ground claims"
        // (run + fire on fabrication) from "no context available" (soft-skip).
        var contactName = snapshot.CharacterState?.PrimaryContactName ?? Roles.Mark;
        var contactRecentMessages = snapshot.RecentHistory?
            .Where(m => string.Equals(m.Role, Roles.Mark, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(m.Role, contactName, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList() ?? new List<string>();

        var artifact = new CognitiveArtifact
        {
            Content                 = thought,
            ProducerKind            = CognitiveProducerKind.InnerThought,
            IntendedSink            = CognitiveOutputSink.PersistedMemory,
            ContactName             = contactName,
            GeneratedAt             = DateTimeOffset.UtcNow,
            PriorAniMessages        = priorThoughts,
            ContactRecentMessages   = contactRecentMessages,
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

    /// <summary>
    /// Internal carrier for the hybrid metadata-recognizer result. Kept
    /// distinct from <see cref="InnerThoughtResult"/> so the recognizer's
    /// fallback path has a unified return type independent of whether the
    /// hybrid or legacy path produced the values.
    /// </summary>
    internal sealed record MetadataRecognitionResult(
        string  Register,
        float   Valence,
        float   Importance,
        string? AssociativeAnchor);
}
