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
    private readonly IRegisterClassifier _registerClassifier;
    private readonly ILogger<InnerThoughtPhase> _log;
    private readonly ICognitiveOutputGate? _outputGate;
    private readonly IEpistemicSubstrateRenderer? _epistemicRenderer;
    private readonly IThoughtShapeClassifier? _shapeClassifier;
    private readonly IInnerThoughtClaimExtractor? _claimExtractor;
    private readonly bool _epistemicFramingEnabled;
    private readonly bool _hybridCycleEnabled;
    private readonly string _hybridMetadataModel;
    private readonly bool _thoughtShapeClassificationEnabled;
    private readonly bool _claimExtractionEnabled;

    /// <summary>
    /// F-1 Phase 3 (2026-08-18): <paramref name="shapeClassifier"/> is
    /// optional. When non-null AND
    /// <see cref="AniOptions.ThoughtShapeClassificationEnabled"/> is true,
    /// each generated thought is classified into a <see cref="ThoughtShape"/>
    /// and the result is written to <see cref="InnerThoughtResult.Shape"/>.
    /// Failure is fail-open — the cycle proceeds using the raw thought text
    /// as-is with <see cref="ThoughtShape.Unclassified"/>.
    ///
    /// <para>
    /// F-3 U4 (2026-08-24): <paramref name="claimExtractor"/> is optional.
    /// When non-null AND <see cref="AniOptions.InnerThoughtClaimExtractionEnabled"/>
    /// is true AND the hybrid cycle is on, a Qwen-14B sidecar pass runs
    /// after composition to extract per-quote attribution claims. Extracted
    /// claims are attached to <see cref="InnerThoughtResult.Claims"/>. Same
    /// fail-open discipline as the metadata recognizer — extractor failure
    /// leaves Claims null and the cycle proceeds normally.
    /// </para>
    /// </summary>
    public InnerThoughtPhase(
        IOllamaClient ollama,
        IRegisterClassifier registerClassifier,
        ILogger<InnerThoughtPhase> log,
        ICognitiveOutputGate? outputGate = null,
        Microsoft.Extensions.Options.IOptions<AniOptions>? aniOptions = null,
        IEpistemicSubstrateRenderer? epistemicRenderer = null,
        IThoughtShapeClassifier? shapeClassifier = null,
        IInnerThoughtClaimExtractor? claimExtractor = null)
    {
        _ollama = ollama;
        _registerClassifier = registerClassifier;
        _log = log;
        _outputGate = outputGate;
        _epistemicRenderer = epistemicRenderer;
        _shapeClassifier = shapeClassifier;
        _claimExtractor = claimExtractor;
        _epistemicFramingEnabled = aniOptions?.Value.EpistemicFramingEnabled ?? false;
        _hybridCycleEnabled = aniOptions?.Value.UseHybridInnerThoughtCycle ?? false;
        _hybridMetadataModel = aniOptions?.Value.HybridInnerThoughtMetadataModel ?? "qwen3:14b";
        _thoughtShapeClassificationEnabled = aniOptions?.Value.ThoughtShapeClassificationEnabled ?? true;
        _claimExtractionEnabled = aniOptions?.Value.InnerThoughtClaimExtractionEnabled ?? true;
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
        // F-5 Phase 2 (2026-08-24) — phase scope tag so log lines emitted
        // inside inner-thought generation render as [cid:.../InnerThought]
        // and are filterable by phase across cycles.
        using var phaseScope = _log.BeginScope(
            new Dictionary<string, object> { ["Phase"] = "InnerThought" });

        var rendererForPrompt = _epistemicFramingEnabled ? _epistemicRenderer : null;
        var thoughtPrompt = PromptBuilder.BuildInnerThoughtPrompt(snapshot, rendererForPrompt);
        // F-2 Phase 1 P5 (2026-08-22, PR #129 review-fix) — WithAttributionKey
        // is safe: preserves empty base system (Modelfile SYSTEM fallback),
        // returns unchanged when history is unattributed, otherwise prepends
        // the attribution framing block. Load-bearing for the 12:04
        // misattribution class.
        var systemPrompt = PromptBuilder.WithAttributionKey(thoughtPrompt.System, snapshot.RecentHistory);
        var thought = await _ollama.InnerMonologueChatAsync(
            systemPrompt, snapshot.RecentHistory, thoughtPrompt.User, ct)
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

        // F-1 Phase 3 (2026-08-18): shape classification. Runs in parallel
        // with metadata recognition when the hybrid path is on, so it adds
        // no serial latency vs the pre-Phase-3 cycle. Fail-open — a
        // classifier failure returns Unclassified and the cycle proceeds.
        var shapeTask = ClassifyShapeAsync(thought, ct);

        // Posture-S+1 hybrid path. Single Qwen call recognizes register,
        // valence, importance, and associative-anchor from the thought v7
        // already produced. Drops the separate reflection call (May 17
        // empirical finding: two-part structure trains continual loops).
        //
        // F-3 U4 (2026-08-24) — added a second Qwen sidecar pass that
        // extracts per-quote attribution claims from the thought. Runs
        // in parallel with the metadata recognizer so it adds no serial
        // latency (subject to Ollama's concurrency configuration —
        // OLLAMA_NUM_PARALLEL controls whether the two Qwen calls actually
        // run concurrently). Fail-open: extractor failure returns empty
        // claims list; the emission then flows through the base
        // IComposerEmission<string> surface at the wrap site instead of
        // the extended IClaimBearingEmission<string>.
        if (_hybridCycleEnabled)
        {
            var runClaimExtraction = _claimExtractionEnabled && _claimExtractor is not null;
            // CharacterStateDoc.Name and PrimaryContactName are non-nullable
            // strings that default to string.Empty (not null), so `?? "Ani"`
            // and `?? "Mark"` would never fire and the extractor would receive
            // empty strings — which its prompt template then maps to "she" /
            // "the caregiver" instead of the intended defaults. Devin PR #139
            // review-fix (2026-08-24): use IsNullOrWhiteSpace, mirroring the
            // pattern in InnerThoughtMetadataPromptCommand.cs:45-46.
            var characterName = string.IsNullOrWhiteSpace(snapshot.CharacterState.Name)
                ? "Ani"
                : snapshot.CharacterState.Name;
            var contactName = string.IsNullOrWhiteSpace(snapshot.CharacterState.PrimaryContactName)
                ? "Mark"
                : snapshot.CharacterState.PrimaryContactName;
            var claimsTask = runClaimExtraction
                ? _claimExtractor!.ExtractAsync(thought, characterName, contactName, ct)
                : Task.FromResult<IReadOnlyList<ContentClaim>>(Array.Empty<ContentClaim>());

            var metadata = await RecognizeMetadataAsync(thought, snapshot, ct)
                .ConfigureAwait(false);
            var shape = await shapeTask.ConfigureAwait(false);
            var claims = await claimsTask.ConfigureAwait(false);
            _log.LogInformation(
                "F1_THOUGHT_SHAPE producer=InnerThoughtPhase shape={Shape} chars={Chars} path=hybrid",
                shape, thought.Length);
            return new InnerThoughtResult(
                Thought:           thought,
                Reflection:        null,
                Valence:           metadata.Valence,
                Register:          metadata.Register,
                Importance:        metadata.Importance,
                AssociativeAnchor: metadata.AssociativeAnchor,
                Shape:             shape,
                Claims:            claims.Count > 0 ? claims : null);
        }

        // Legacy three-call path. Kept callable for safe rollout and so
        // empirical comparison data remains collectable.
        var valence = await ScoreRelationalValenceAsync(thought, snapshot.CharacterState, ct)
            .ConfigureAwait(false);
        var reflection = await ReflectOnThoughtAsync(thought, snapshot, ct).ConfigureAwait(false);
        var legacyShape = await shapeTask.ConfigureAwait(false);
        _log.LogInformation(
            "F1_THOUGHT_SHAPE producer=InnerThoughtPhase shape={Shape} chars={Chars} path=legacy",
            legacyShape, thought.Length);

        return new InnerThoughtResult(thought, reflection, valence, Shape: legacyShape);
    }

    /// <summary>
    /// F-1 Phase 3 (2026-08-18) — classify the shape of a generated thought.
    /// Returns <see cref="ThoughtShape.Unclassified"/> when the classifier is
    /// unavailable or disabled. Kicked off in parallel with the metadata
    /// call so it contributes no serial latency (subject to Ollama's
    /// concurrency configuration — see PR #112 Devin note about
    /// OLLAMA_NUM_PARALLEL).
    ///
    /// <para>
    /// Reviewer feedback PR #112 (2026-08-18): removed the wrapping try/catch
    /// that swallowed all non-OCE exceptions. The classifier's own contract
    /// (<see cref="IThoughtShapeClassifier"/>) already guarantees fail-open
    /// return of Unclassified on transport / parse / timeout failures — a
    /// second belt-and-suspenders catch here was masking real defects in
    /// the classifier implementation (per global CLAUDE.md rule #4:
    /// don't swallow with generic catch). Any exception escaping the
    /// classifier IS a bug and must surface for diagnosis.
    /// </para>
    /// </summary>
    internal Task<ThoughtShape> ClassifyShapeAsync(string thought, CancellationToken ct)
    {
        if (_shapeClassifier is null || !_thoughtShapeClassificationEnabled)
            return Task.FromResult(ThoughtShape.Unclassified);

        return _shapeClassifier.ClassifyAsync(thought, ct);
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
        // Singular-surface register classification (2026-08-12): register
        // now flows through IRegisterClassifier so every producer in the
        // system uses one prompt + one model + one taxonomy.
        var register = await _registerClassifier.ClassifyAsync(thought, ct).ConfigureAwait(false);

        var (system, user) = PromptBuilder.BuildInnerThoughtMetadataPrompt(thought, snapshot, register);

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
        // Fallback register: Longing is the canonical family for the old
        // "Wistful" legacy default (per QwenRegisterClassifier.NormalizeToCanonical
        // and ImpactCategoryDefaults.ToRegisterFamily).
        return new MetadataRecognitionResult(
            Register:          "Longing",
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
