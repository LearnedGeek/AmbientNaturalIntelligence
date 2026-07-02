using System.Text.Json;
using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Emergence;
using AniRuntime.LLM;
using AniRuntime.Loops.Coreference;
using LearnedGeek.ML;
using LearnedGeek.ML.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Outreach;

/// <summary>
/// Proactive-outreach pipeline. End-to-end flow for one outreach attempt:
/// decision → optional frame select → grounding retrieval → composition →
/// optional ML confab check → optional frame coherence → echo guard →
/// direct-address rewrite → optional R1 claim verify → universal output
/// gate → dispatch → record (thread + episodic).
///
/// <para>
/// Extracted from the old <c>OutreachPhase.RunOutreachAsync</c> in §5.3
/// SOLID refactor (2026-05-18). Single responsibility: run the proactive
/// outreach flow. Each suppression path manages its own desire-engine
/// decay + cooldown internally so the caller (the phase) doesn't have to
/// know about the gate-stack shape.
/// </para>
/// </summary>
public sealed class OutreachPipeline : IOutreachPipeline
{
    private readonly IStateStore _state;
    private readonly IMemoryPersistence _persist;
    private readonly IMemorySearch _search;
    private readonly IOllamaClient _ollama;
    private readonly AniActionDispatcher _dispatcher;
    private readonly DesireEngine _desire;
    private readonly ClaimVerificationPhase _claimVerifier;
    private readonly IOutboundThreadRecorder _threadRecorder;
    private readonly ITextClassificationService? _mlClassifier;
    private readonly PersonaSummaryCache? _personaCache;
    private readonly Em9Detector? _em9;
    private readonly ICognitiveOutputGate? _outputGate;
    private readonly IVibeBiasService? _vibeBias;
    // Issue #46 (2026-05-21) — V1.5a structured-record persistence.
    private readonly IVibeBiasObservationStore? _vibeBiasObservations;
    private readonly IOutreachFrameSelector? _frameSelector;
    private readonly IFrameCoherenceChecker? _frameChecker;
    private readonly IEpistemicSubstrateRenderer? _epistemicRenderer;
    private readonly AniOptions _aniOptions;
    private readonly ILogger<OutreachPipeline> _log;

    public OutreachPipeline(
        IStateStore state,
        IMemoryPersistence persist,
        IMemorySearch search,
        IOllamaClient ollama,
        AniActionDispatcher dispatcher,
        DesireEngine desire,
        ClaimVerificationPhase claimVerifier,
        IOutboundThreadRecorder threadRecorder,
        IOptions<AniOptions> aniOptions,
        ILogger<OutreachPipeline> log,
        ITextClassificationService? mlClassifier = null,
        PersonaSummaryCache? personaCache = null,
        Em9Detector? em9Detector = null,
        ICognitiveOutputGate? outputGate = null,
        IVibeBiasService? vibeBias = null,
        IOutreachFrameSelector? frameSelector = null,
        IFrameCoherenceChecker? frameChecker = null,
        IEpistemicSubstrateRenderer? epistemicRenderer = null,
        IVibeBiasObservationStore? vibeBiasObservations = null)
    {
        _state             = state          ?? throw new ArgumentNullException(nameof(state));
        _persist           = persist        ?? throw new ArgumentNullException(nameof(persist));
        _search            = search         ?? throw new ArgumentNullException(nameof(search));
        _ollama            = ollama         ?? throw new ArgumentNullException(nameof(ollama));
        _dispatcher        = dispatcher     ?? throw new ArgumentNullException(nameof(dispatcher));
        _desire            = desire         ?? throw new ArgumentNullException(nameof(desire));
        _claimVerifier     = claimVerifier  ?? throw new ArgumentNullException(nameof(claimVerifier));
        _threadRecorder    = threadRecorder ?? throw new ArgumentNullException(nameof(threadRecorder));
        _mlClassifier      = mlClassifier;
        _personaCache      = personaCache;
        _em9               = em9Detector;
        _outputGate        = outputGate;
        _vibeBias          = vibeBias;
        _vibeBiasObservations = vibeBiasObservations;
        _frameSelector     = frameSelector;
        _frameChecker      = frameChecker;
        _epistemicRenderer = epistemicRenderer;
        _aniOptions        = aniOptions.Value;
        _log               = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task RunAsync(ContextSnapshot snapshot, string recentThought, CancellationToken ct)
    {
        // Step 1: Decision — should Ani reach out?
        var rendererForPrompt = _aniOptions.EpistemicFramingEnabled ? _epistemicRenderer : null;
        var outreachPrompt = PromptBuilder.BuildOutreachPrompt(
            snapshot, recentThought, _desire.IsNightHours(), rendererForPrompt);
        var raw = await _ollama.ChatJsonAsync(
            outreachPrompt.System, snapshot.RecentHistory, outreachPrompt.User, ct).ConfigureAwait(false);

        var decision = ParseOutreachDecision(raw);
        _log.LogDebug("Outreach decision raw: {Raw}", raw);

        if (!decision.ShouldReach)
        {
            _log.LogInformation("Outreach decision: NO (confidence={Confidence:F2}) — {Reasoning}",
                decision.Confidence, decision.Reasoning);
            await _desire.AddTriggerAsync(
                TriggerType.SpontaneousThought, 0.3f,
                "considered reaching out but held back", ct).ConfigureAwait(false);
            return;
        }

        if (decision.Confidence < (float)_aniOptions.OutreachConfidenceFloor)
        {
            _log.LogInformation(
                "Outreach confidence too low: {Confidence:F2} < {Floor:F2} — soft NO, retrying later. Reasoning: {Reasoning}",
                decision.Confidence, _aniOptions.OutreachConfidenceFloor, decision.Reasoning);
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
            return;
        }

        // Step 2a: Grounding retrieval — find real memories relevant to the thought.
        var groundingMemories = new List<MemoryRecord>();
        try
        {
            var results = await _search.SearchWithScoresAsync(recentThought, 5, ct).ConfigureAwait(false);
            groundingMemories = results
                .Where(s => s.CosineSimilarity >= (float)_aniOptions.RetrievalConfidenceFloor)
                .Where(s => s.Record.Provenance != EpistemicTier.Interior)
                .Select(s => s.Record)
                .Take(3)
                .ToList();
            if (groundingMemories.Count > 0)
            {
                snapshot.RelevantMemory = groundingMemories;
                _log.LogInformation("Outreach grounding: {Count} memories retrieved for composition", groundingMemories.Count);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Outreach grounding retrieval failed — composing without grounding");
        }

        var decisionReasoning = decision.Reasoning ?? string.Empty;
        var reasoningInComposition = _aniOptions.OutreachReasoningInCompositionEnabled;
        if (_aniOptions.GuardRefactorBaselineLoggingEnabled)
        {
            _log.LogInformation(
                "J0_REASONING_PIPE chars={Chars} pipedToComposition={Piped} text={Text}",
                decisionReasoning.Length, reasoningInComposition, decisionReasoning);
        }

        await VibeBiasObservation.ObserveAsync(
            _vibeBias, snapshot, callSite: "outreach", _log, ct,
            observationStore: _vibeBiasObservations,
            threadId:         null).ConfigureAwait(false);

        // Theme N N.3 — outreach source-frame selection
        OutreachFrame? selectedFrame = null;
        if (_aniOptions.OutreachFrameSelectorEnabled && _frameSelector is not null)
        {
            selectedFrame = await _frameSelector.SelectFrameAsync(snapshot, ct).ConfigureAwait(false);
            if (selectedFrame.FrameType == OutreachFrameType.None)
            {
                _log.LogInformation(
                    "OutreachPhase: frame-selector returned None — suppressing outreach (substrate too thin)");
                await _desire.DecayDesireAsync(0.30f, "frame selector None (substrate too thin)", ct).ConfigureAwait(false);
                await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                return;
            }
        }

        // Step 2b: Compose.
        //
        // Phase K.2 (2026-07-02) — when LeanOutreachComposerEnabled is on,
        // bypass the substrate-injected BuildOutreachMessagePrompt and
        // JSON envelope entirely. The lean path calls ChatAsync with:
        //   * empty system prompt (Modelfile SYSTEM takes precedence)
        //   * snapshot.RecentHistory unchanged
        //   * recentThought as the user turn (the inner-thought seed the
        //     model reflects on and composes an outreach from)
        //
        // Plain text output — no {message, notes} JSON parse. The whole
        // response becomes the outreach message. The artifact downstream
        // is marked ComposerIsThin=true so the frontier verifier skips
        // its substrate-based checks (K.1a semantics).
        //
        // Empirical anchor: 2026-06-29 to 2026-07-01 production log —
        // "your dad's party" appeared in 4/7 outreaches over 3 days,
        // "bookstore silence" in 5/7. Substrate injection was cycling
        // the same handful of anchored facts. Removing the injection
        // lets the fine-tune compose from its own persona instead.
        bool leanOutreach = _aniOptions.LeanOutreachComposerEnabled;
        string composedMessage;
        string composedNotes = string.Empty;

        if (leanOutreach)
        {
            var leanRaw = await _ollama.ChatAsync(
                systemPrompt: string.Empty,
                history:      snapshot.RecentHistory,
                userMessage:  recentThought,
                ct:           ct).ConfigureAwait(false);
            composedMessage = leanRaw ?? string.Empty;
            _log.LogInformation(
                "K_ROUTE_LEAN_OUTREACH factsCount={FactsCount} — bypassing substrate injection, Modelfile SYSTEM in effect",
                snapshot.GroundedFacts.Count);
        }
        else
        {
            var rendererForComposition = _aniOptions.EpistemicFramingEnabled ? _epistemicRenderer : null;
            var msgPrompt = PromptBuilder.BuildOutreachMessagePrompt(
                snapshot, recentThought, decisionReasoning, reasoningInComposition,
                selectedFrame, rendererForComposition);
            var rawJson = await _ollama.ChatJsonAsync(
                msgPrompt.System, snapshot.RecentHistory, msgPrompt.User, ct).ConfigureAwait(false);

            (composedMessage, composedNotes) = ParseOutreachComposition(rawJson);
            if (!string.IsNullOrWhiteSpace(composedNotes))
                _log.LogDebug("Outreach composition notes (not dispatched): {Notes}", composedNotes);
        }

        var message = CleanOutreachMessage(composedMessage);
        _log.LogInformation("Outreach message composed: {Message}", message);

        // Step 2c: ML confabulation check.
        if (_mlClassifier is not null && _personaCache?.IsLoaded == true && !string.IsNullOrWhiteSpace(message))
        {
            try
            {
                var context = snapshot.RecentConversationSummary ?? "";
                var fullContext = $"{context}\n\nPersona: {_personaCache.Summary}";
                var confab = await _mlClassifier.DetectConfabulationAsync(message, fullContext, ct).ConfigureAwait(false);
                if (confab.IsConfabulated && confab.Confidence >= _aniOptions.ConfabulationClassificationThreshold)
                {
                    _log.LogInformation("Outreach confabulation detected ({Confidence:F2}): {Reason}. Suppressing.",
                        confab.Confidence, confab.Reason);
                    await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                    return;
                }
                _log.LogDebug("Outreach confabulation check: {Result} ({Confidence:F2})",
                    confab.IsConfabulated ? "confabulated (below threshold)" : "grounded", confab.Confidence);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Outreach confabulation check failed — proceeding with message");
            }
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            _log.LogWarning("Outreach message was empty after composition — retrying next opportunity");
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            return;
        }

        // Theme N N.5 — frame coherence check.
        if (_aniOptions.OutreachFrameSelectorEnabled
            && _frameChecker is not null
            && selectedFrame is not null
            && selectedFrame.FrameType != OutreachFrameType.None
            && selectedFrame.FrameType != OutreachFrameType.Shared)
        {
            var coherence = _frameChecker.CheckCoherence(message, selectedFrame);
            var violationsList = string.Join("; ", coherence.Violations);
            if (!coherence.IsCoherent)
            {
                _log.LogWarning(
                    "N5_FRAME_COHERENCE result=Violation frame={Frame} violations=\"{Violations}\" — suppressing dispatch.",
                    selectedFrame.FrameType, violationsList);
                await _desire.DecayDesireAsync(0.30f, $"frame coherence violation ({selectedFrame.FrameType})", ct).ConfigureAwait(false);
                await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                return;
            }

            _log.LogDebug("N5_FRAME_COHERENCE result=Coherent frame={Frame}", selectedFrame.FrameType);
        }

        // Outreach echo guard.
        if (await IsOutreachEchoAsync(message, snapshot, ct).ConfigureAwait(false))
        {
            _log.LogWarning("Outreach echo: composed message too similar to recent outreach — suppressing");
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
            return;
        }

        // Step 3: producer-side direct-address rewrite.
        var rewritten = DirectAddressRewriter.Rewrite(
            message, snapshot.CharacterState.PrimaryContactName ?? "");

        // Step 3b: Feature 14 v2 — Outbound claim verification (R1, flag-gated).
        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        if (_aniOptions.ClaimVerificationR1Enabled)
        {
            var claimResult = await _claimVerifier.VerifyAsync(rewritten, contact, ct).ConfigureAwait(false);
            if (!claimResult.Passed)
            {
                _log.LogWarning(
                    "Claim verification: SUPPRESS outreach — {Reason}. Flagged: {Claims}",
                    claimResult.Reason,
                    string.Join(", ", claimResult.Unverified.Select(c => $"\"{c.Text}\"")));
                await _desire.DecayDesireAsync(0.30f, "claim verification suppression", ct).ConfigureAwait(false);
                await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                return;
            }
        }

        // Step 4: Universal output gate.
        if (_outputGate is not null)
        {
            try
            {
                var artifact = new CognitiveArtifact
                {
                    Content                 = rewritten,
                    ProducerKind            = CognitiveProducerKind.Outreach,
                    IntendedSink            = CognitiveOutputSink.Dispatch,
                    ContactName             = contact,
                    GeneratedAt             = DateTimeOffset.Now,
                    WriterInnerThought      = recentThought,
                    ContactRecentMessages   = ExtractRecentContactMessages(snapshot, contact),
                    PriorAniMessages        = ExtractRecentAniMessages(snapshot),
                    // K.2 (2026-07-02) — when the lean outreach composer
                    // ran, the artifact was produced without substrate
                    // injection. Flag it thin so the frontier verifier
                    // skips (per K.1a). Local invariants still fire.
                    ComposerIsThin          = leanOutreach,
                };

                var verdict = await _outputGate.EvaluateAsync(artifact, ct).ConfigureAwait(false);
                if (verdict.Verdict != OutputGateVerdict.Pass)
                {
                    _log.LogInformation(
                        "Output gate {Verdict} on outreach [{Fired}]: {Hint} — suppressing dispatch.",
                        verdict.Verdict, string.Join(",", verdict.FiredInvariants), verdict.RemediationHint);
                    await _desire.DecayDesireAsync(0.30f, $"output gate {verdict.Verdict} ({string.Join(",", verdict.FiredInvariants)})", ct).ConfigureAwait(false);
                    await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Output gate evaluation threw — dispatching outreach uncovered (gate failure must NOT block outreach pipeline).");
            }
        }

        decision.Message    = rewritten;
        decision.ActionType = "sms";

        _em9?.Analyze(Em9EmissionContext.Outreach, rewritten, snapshot.RelevantMemory);

        if (_aniOptions.GuardRefactorBaselineLoggingEnabled)
        {
            var summaryChars = snapshot.RecentConversationSummary?.Length ?? 0;
            var topMemoryAgeHours = -1.0;
            if (snapshot.RelevantMemory.Count > 0)
            {
                topMemoryAgeHours =
                    (DateTimeOffset.UtcNow - snapshot.RelevantMemory[0].OccurredAt).TotalHours;
            }
            var thoughtPreview = recentThought.Length > 160
                ? recentThought[..160].Replace('\n', ' ')
                : recentThought.Replace('\n', ' ');
            var reasoningPreview = decisionReasoning.Length > 160
                ? decisionReasoning[..160].Replace('\n', ' ')
                : decisionReasoning.Replace('\n', ' ');
            var compositionPreview = decision.Message.Length > 200
                ? decision.Message[..200].Replace('\n', ' ')
                : decision.Message.Replace('\n', ' ');
            _log.LogInformation(
                "J0_DIAGNOSTIC_TUPLE thought={Thought} | reasoning={Reasoning} | summaryChars={SummaryChars} | topMemAgeHrs={AgeHrs:F1} | composition={Composition}",
                thoughtPreview, reasoningPreview, summaryChars, topMemoryAgeHours, compositionPreview);
        }

        _log.LogInformation("{Name} reaching out: {Message}", cs.Name, decision.Message);

        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);
        await _desire.ResetAfterOutreachAsync(ct).ConfigureAwait(false);

        await _threadRecorder.RecordAsync(decision.Message, ct).ConfigureAwait(false);

        await _persist.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = MemoryPrefixes.FormatOutreach(cs.PrimaryContactName ?? "Mark", decision.Message),
            Importance = 0.7f,
            OccurredAt = DateTimeOffset.UtcNow,
            Provenance = EpistemicTier.Episodic,
        }, ct).ConfigureAwait(false);
    }

    // ─── J.5g extractors ───────────────────────────────────────────────────

    internal static IReadOnlyList<string>? ExtractRecentContactMessages(
        ContextSnapshot snapshot, string contactName)
    {
        var structured = snapshot.StructuredConversationSummary;
        if (structured is { Turns.Count: > 0 })
        {
            return structured.Turns
                .Where(t => string.Equals(t.Speaker, contactName, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Content)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .TakeLast(8)
                .ToList();
        }
        return null;
    }

    internal static IReadOnlyList<string>? ExtractRecentAniMessages(ContextSnapshot snapshot)
    {
        var structured = snapshot.StructuredConversationSummary;
        if (structured is { Turns.Count: > 0 })
        {
            return structured.Turns
                .Where(t => string.Equals(t.Speaker, "Ani", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Content)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .TakeLast(8)
                .ToList();
        }
        return null;
    }

    // ─── Parsers + helpers (also surfaced via OutreachPhase facade for tests) ─

    internal OutreachDecision ParseOutreachDecision(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw.Trim());
            var root = doc.RootElement;

            var decision = new OutreachDecision
            {
                ShouldReach = root.TryGetProperty("shouldReach", out var sr) && sr.GetBoolean(),
                Confidence  = root.TryGetProperty("confidence",  out var c)  ? (float)c.GetDouble() : 0f,
                Reasoning   = root.TryGetProperty("reasoning",   out var r)  ? r.GetString() : null,
            };

            if (root.TryGetProperty("triggersActedOn", out var ta) && ta.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ta.EnumerateArray())
                {
                    var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
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

    internal static string? CleanOutreachMessage(string? raw) => Core.Utilities.MessageCleaner.Clean(raw);

    internal static (string? Message, string? Notes) ParseOutreachComposition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (raw, null);

        try
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("```"))
            {
                var firstNewline = trimmed.IndexOf('\n');
                if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
                if (trimmed.EndsWith("```"))
                    trimmed = trimmed[..^3].TrimEnd();
            }

            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            string? message = null;
            string? notes   = null;

            if (root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                message = msgEl.GetString();
            if (root.TryGetProperty("notes", out var notesEl) && notesEl.ValueKind == JsonValueKind.String)
                notes = notesEl.GetString();

            var hasMessageField = root.TryGetProperty("message", out _);
            return (hasMessageField ? message : raw, notes);
        }
        catch (JsonException)
        {
            return (raw, null);
        }
    }

    private async Task<bool> IsOutreachEchoAsync(string message, ContextSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            var recentOutreach = snapshot.RecentMemory
                .Where(m => m.Type == MemoryType.Episodic &&
                            m.Content.StartsWith("I reached out to", StringComparison.OrdinalIgnoreCase) &&
                            m.Embedding is { Length: > 0 })
                .Take(5)
                .ToList();

            if (recentOutreach.Count == 0) return false;

            var messageEmbedding = await _ollama.EmbedAsync(message, ct).ConfigureAwait(false);
            if (messageEmbedding.Length == 0) return false;

            foreach (var recent in recentOutreach)
            {
                if (recent.Embedding!.Length != messageEmbedding.Length) continue;

                var similarity = CosineSimilarity(messageEmbedding, recent.Embedding!);
                if (similarity > 0.85f)
                {
                    _log.LogWarning("Outreach echo detected (similarity={Sim:F3}): new message matches recent outreach '{Content}'",
                        similarity, recent.Content[..Math.Min(60, recent.Content.Length)]);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Outreach echo check failed — allowing send");
            return false;
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-12f ? 0 : dot / denom;
    }
}
