using System.Text.Json;
using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Emergence;
using AniRuntime.LLM;
using LearnedGeek.ML;
using LearnedGeek.ML.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Handles outbound message creation: outreach decision, composition, pronoun fix,
/// coherence gate, dispatch, reactive sharing, and silence recording.
/// </summary>
public class OutreachPhase
{
    private readonly IStateStore _state;
    private readonly IMemoryPersistence _persist;
    private readonly IMemorySearch _search;
    private readonly IOllamaClient _ollama;
    private readonly AniActionDispatcher _dispatcher;
    private readonly DesireEngine _desire;
    private readonly ClaimVerificationPhase _claimVerifier;
    private readonly IConversationService _conversations;
    private readonly ITextClassificationService? _mlClassifier;
    private readonly PersonaSummaryCache? _personaCache;
    private readonly Em9Detector? _em9;
    private readonly ICognitiveOutputGate? _outputGate;
    private readonly IVibeBiasService? _vibeBias;
    private readonly AniOptions _aniOptions;
    private readonly ILogger<OutreachPhase> _log;

    // Reactive share rate limiting — resets daily
    private int  _reactiveShareCount;
    private DateTimeOffset _reactiveShareDay = DateTimeOffset.MinValue;

    // Feature 3: Rate-limit silence choice recording — once per 4 hours max
    private DateTimeOffset _lastSilenceRecordedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan SilenceRecordCooldown = TimeSpan.FromHours(4);

    public OutreachPhase(
        IStateStore state,
        IMemoryPersistence persist,
        IMemorySearch search,
        IOllamaClient ollama,
        AniActionDispatcher dispatcher,
        DesireEngine desire,
        ClaimVerificationPhase claimVerifier,
        IConversationService conversations,
        IOptions<AniOptions> aniOptions,
        ILogger<OutreachPhase> log,
        ITextClassificationService? mlClassifier = null,
        PersonaSummaryCache? personaCache = null,
        Em9Detector? em9Detector = null,
        ICognitiveOutputGate? outputGate = null,
        IVibeBiasService? vibeBias = null)
    {
        _state = state;
        _persist = persist;
        _search = search;
        _ollama = ollama;
        _dispatcher = dispatcher;
        _desire = desire;
        _claimVerifier = claimVerifier;
        _conversations = conversations;
        _mlClassifier = mlClassifier;
        _personaCache = personaCache;
        _em9 = em9Detector;
        _outputGate = outputGate;
        _vibeBias = vibeBias;
        _aniOptions = aniOptions.Value;
        _log = log;
    }

    /// <summary>
    /// Apr 29, 2026 (Theme E): outbound conversation_messages invariant fix.
    /// Every Ani-outbound — outreach OR reactive share — must enter the active
    /// conversation thread so subsequent cycles' structured per-speaker summary
    /// (Theme J.2) sees what Ani sent. Without this, follow-up questions from
    /// Mark land in cycles where Ani has no thread-context for her own outbound
    /// and confabulates *"you made that up"* responses (Apr 29 09:04 Anxietyland
    /// trace + Apr 29 10:24 compounding instance).
    ///
    /// Get-or-create semantics: if an active thread exists, use it; if not,
    /// create one with InitiatedBy=Ani (this outbound IS the thread initiation).
    /// Failures in this path log a warning and continue — the dispatch already
    /// succeeded and we don't want a thread-write hiccup to bubble up as a
    /// cycle failure.
    /// </summary>
    internal async Task RecordOutboundInThreadAsync(string content, CancellationToken ct)
    {
        try
        {
            var thread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
            if (thread is null)
            {
                thread = new ConversationThread
                {
                    InitiatedBy   = Roles.Ani,
                    StartedAt     = DateTimeOffset.UtcNow,
                    LastMessageAt = DateTimeOffset.UtcNow,
                };
                await _conversations.SaveThreadAsync(thread, ct).ConfigureAwait(false);
                _log.LogDebug("Outbound created new conversation thread: {ThreadId}", thread.Id);
            }

            await _conversations.AddMessageAsync(thread.Id, new ConversationMessage
            {
                Role    = Roles.Ani,
                Content = content,
                SentAt  = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record outbound in conversation thread — dispatch already succeeded, continuing");
        }
    }

    public async Task RunOutreachAsync(
        ContextSnapshot snapshot, string recentThought, CancellationToken ct)
    {
        // Apr 21, 2026 — Outreach disable flag (Option C, commit 2).
        // When OutreachEnabled=false, the proactive outreach path short-circuits at
        // entry: no decision call, no composition, no dispatch. Inbound conversation
        // replies (ConversationReplyPhase) are unaffected — Mark can still reach her
        // and she can still respond. This is a proactive-outreach lockdown only.
        //
        // Intended to be flipped off while the outbound LLM claim verification
        // (Feature 14 v2) is being built, then flipped back on once verification is
        // deployed. Flip via appsettings.json or appsettings.Development.json:
        //   "Ani": { "OutreachEnabled": false }
        if (!_aniOptions.OutreachEnabled)
        {
            _log.LogInformation(
                "Outreach disabled: OutreachEnabled=false in config — skipping proactive outreach composition and dispatch");
            return;
        }

        // Step 1: Decision — should Ani reach out? (JSON, no message required)
        var outreachPrompt = PromptBuilder.BuildOutreachPrompt(snapshot, recentThought, _desire.IsNightHours());
        var raw            = await _ollama.ChatJsonAsync(
            outreachPrompt.System, snapshot.RecentHistory, outreachPrompt.User, ct)
            .ConfigureAwait(false);

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

        // Feature 12: Confidence threshold
        if (decision.Confidence < (float)_aniOptions.OutreachConfidenceFloor)
        {
            _log.LogInformation(
                "Outreach confidence too low: {Confidence:F2} < {Floor:F2} — soft NO, retrying later. Reasoning: {Reasoning}",
                decision.Confidence, _aniOptions.OutreachConfidenceFloor, decision.Reasoning);
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
            return;
        }

        // Step 2a: Grounding retrieval — find real memories relevant to the thought.
        // The thought triggers the desire to reach out. The memory provides the content.
        // "Inner thought as trigger, not content."
        var groundingMemories = new List<MemoryRecord>();
        try
        {
            var results = await _search.SearchWithScoresAsync(recentThought, 5, ct).ConfigureAwait(false);
            groundingMemories = results
                .Where(s => s.CosineSimilarity >= (float)_aniOptions.RetrievalConfidenceFloor)
                .Where(s => s.Record.Type != MemoryType.InnerThought) // Don't ground from other generated thoughts
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

        // Theme J Phase J.0 (Apr 24, 2026): instrument the decision-reasoning
        // pipe before composition. Theme J Phase J.1 (Apr 27, 2026): the
        // reasoning is no longer piped into composition by default; the
        // OutreachReasoningInCompositionEnabled flag gates the legacy
        // behaviour for rollback. The instrumentation still logs the
        // reasoning text + its size for observability.
        var decisionReasoning = decision.Reasoning ?? string.Empty;
        var reasoningInComposition = _aniOptions.OutreachReasoningInCompositionEnabled;
        if (_aniOptions.GuardRefactorBaselineLoggingEnabled)
        {
            _log.LogInformation(
                "J0_REASONING_PIPE chars={Chars} pipedToComposition={Piped} text={Text}",
                decisionReasoning.Length, reasoningInComposition, decisionReasoning);
        }

        // Vibe Loop V1.5a (May 2, 2026) — observational-only telemetry pass.
        // Logs V15_BIAS_* lines describing what V1.5b WOULD surface from the
        // closed-conversation-record substrate; the prompt below is unchanged.
        // Self-regulation framing: the bias is computed from Ani's-delta
        // valence on each prior record, never from Mark's-delta. See
        // docs/spec/ANI-VibeLoop-V1.5-Retrieval-Time-Biasing-Plan.md §V1.5a.
        await VibeBiasObservation.ObserveAsync(
            _vibeBias, snapshot, callSite: "outreach", _log, ct).ConfigureAwait(false);

        // Step 2b: Compose — free-text message generation (no JSON constraint)
        var msgPrompt = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, recentThought, decisionReasoning, reasoningInComposition);
        var message = await _ollama.ChatAsync(
            msgPrompt.System, snapshot.RecentHistory, msgPrompt.User, ct)
            .ConfigureAwait(false);

        message = CleanOutreachMessage(message);
        _log.LogInformation("Outreach message composed: {Message}", message);

        // Step 2c: ML confabulation check on composed message (same gate as conversation)
        if (_mlClassifier is not null && _personaCache?.IsLoaded == true && !string.IsNullOrWhiteSpace(message))
        {
            try
            {
                var context = snapshot.RecentConversationSummary ?? "";
                var fullContext = $"{context}\n\nPersona: {_personaCache.Summary}";
                var confab = await _mlClassifier.DetectConfabulationAsync(message, fullContext, ct)
                    .ConfigureAwait(false);
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

        // Outreach echo guard: check against recent outreach messages to prevent duplicates
        // across cycles. The conversation echo guard only checks within a thread — this
        // catches the same message composed in separate outreach cycles.
        if (await IsOutreachEchoAsync(message, snapshot, ct).ConfigureAwait(false))
        {
            _log.LogWarning("Outreach echo: composed message too similar to recent outreach — suppressing");
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
            return;
        }

        // Step 3: Light pronoun fix — only if third-person leaked through
        var rewritten = await FixPronounsIfNeeded(message, snapshot.CharacterState, ct)
            .ConfigureAwait(false);

        // Step 3b: Feature 14 v2 — Outbound claim verification (Apr 22, 2026).
        // Extracts claims about the contact from the composed message and corroborates
        // each against Facts tier + anchored + inbound Mark messages. On failure the
        // dispatch is suppressed; the model is never told it was wrong. See
        // ClaimVerificationPhase.cs for the full discipline.
        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        var claimResult = await _claimVerifier.VerifyAsync(rewritten, contact, ct).ConfigureAwait(false);
        if (!claimResult.Passed)
        {
            _log.LogWarning(
                "Claim verification: SUPPRESS outreach — {Reason}. Flagged: {Claims}",
                claimResult.Reason,
                string.Join(", ", claimResult.Unverified.Select(c => $"\"{c.Text}\"")));
            await _desire.DecayDesireAsync(0.30f, "claim verification suppression", ct)
                .ConfigureAwait(false);
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            return;
        }

        // Step 4: Universal output gate (J.5b/c/g — May 2, 2026).
        //
        // Pre-J.5g this step called the standalone EvaluateCoherenceAsync (the
        // pipeline-scoped Door A/B/C check). May 2 09:08 Mark: *"this is a case
        // of a gate applying only on a single pipeline again where it should be
        // more universal."* The InnerThoughtBleedInvariant (J.5g) hoists Door C
        // semantics onto the gate; sibling invariants (anti-parrot, prompt-
        // template-leak, confabulation) also fire here. Outreach now routes
        // through the same dispatch-boundary gate as ConversationReply,
        // VoiceTurnPipeline, and Reflection.
        //
        // Suppression behavior preserved: on Remediate/Fail verdict, decay
        // desire + apply cooldown (the existing outreach-suppress shape) and
        // return. We do NOT regenerate on outreach gate-fail because
        // outreach is initiated by Ani — there's no pending user message
        // demanding a reply, so dropping the outreach is the safe move.
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
                    WriterInnerThought      = recentThought,
                    ContactRecentMessages   = ExtractRecentContactMessages(snapshot, contact),
                    PriorAniMessages        = ExtractRecentAniMessages(snapshot),
                };

                var verdict = await _outputGate.EvaluateAsync(artifact, ct).ConfigureAwait(false);
                if (verdict.Verdict != OutputGateVerdict.Pass)
                {
                    _log.LogInformation(
                        "Output gate {Verdict} on outreach [{Fired}]: {Hint} — suppressing dispatch.",
                        verdict.Verdict, string.Join(",", verdict.FiredInvariants), verdict.RemediationHint);
                    await _desire.DecayDesireAsync(0.30f, $"output gate {verdict.Verdict} ({string.Join(",", verdict.FiredInvariants)})", ct)
                        .ConfigureAwait(false);
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

        // EM9 — Longitudinal Memory Compounding (Apr 27, 2026, backlog 15.15).
        // Scan the retrieval pool that fed this composition for >90-day-old
        // memories that surfaced via architectural mechanisms (Anchored
        // tier or reflection synthesis). Each candidate is logged for the
        // longitudinal trend analysis. Pure observation — does not affect
        // dispatch. The detector itself is null-safe to keep tests + DI
        // configurations that don't wire EM9 working unchanged.
        _em9?.Analyze(Em9EmissionContext.Outreach, rewritten, snapshot.RelevantMemory);

        // Theme J Phase J.0 (Apr 24, 2026): DIAGNOSTIC_TUPLE pre-dispatch.
        // Captures the four key fields stitched together in one log line per
        // outreach-composition cycle so post-hoc analysis of a parrot /
        // time-confab / attribution-drift event is a single grep rather than
        // a cross-log correlation exercise. The four fields align with the
        // J.1/J.2/J.3 upstream-fix surfaces.
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

        // Apr 29, 2026 (Theme E): record outbound in conversation thread so
        // subsequent cycles see it in the structured per-speaker summary.
        await RecordOutboundInThreadAsync(decision.Message, ct).ConfigureAwait(false);

        await _persist.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = MemoryPrefixes.FormatOutreach(cs.PrimaryContactName ?? "Mark", decision.Message),
            Importance = 0.7f,
            OccurredAt = DateTimeOffset.UtcNow,
            // Epistemic Grounding (Apr 10): Dispatched outreach is a verbatim
            // record of what Ani sent — Episodic tier. Conversation continuity,
            // not factual grounding about Mark's world.
            Provenance = EpistemicTier.Episodic,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks for high-relevance RSS items that the contact would care about and shares
    /// them directly — bypassing the desire engine. Rate-limited to prevent spam.
    /// Returns true if a share was sent (cycle should end), false otherwise.
    /// </summary>
    public async Task<bool> TryReactiveShareAsync(
        List<PerceptionEvent> perceptions, CharacterStateDoc charState, CancellationToken ct)
    {
        var threshold = (float)_aniOptions.ReactiveShareThreshold;
        var shareable = perceptions
            .Where(p => p.SourceName == "rss" && p.ContactRelevance >= threshold)
            .OrderByDescending(p => p.ContactRelevance)
            .FirstOrDefault();

        if (shareable is null)
            return false;

        // Nobody shares news articles at 3 AM
        if (_desire.IsNightHours())
        {
            _log.LogDebug("Reactive share blocked — night hours");
            return false;
        }

        // Reset daily counter if the day has rolled over
        var today = DateTimeOffset.Now.Date;
        if (_reactiveShareDay.Date != today)
        {
            _reactiveShareCount = 0;
            _reactiveShareDay = DateTimeOffset.Now;
        }

        if (_reactiveShareCount >= _aniOptions.MaxReactiveSharesPerDay)
        {
            _log.LogDebug("Reactive share blocked — daily limit ({Limit}) reached", _aniOptions.MaxReactiveSharesPerDay);
            return false;
        }

        // Respect a shorter cooldown for reactive shares
        var state = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        var sinceLastOutreach = DateTimeOffset.UtcNow - state.LastOutreach;
        if (sinceLastOutreach.TotalMinutes < _aniOptions.ReactiveShareCooldownMinutes)
        {
            _log.LogDebug("Reactive share blocked — only {Minutes:F0} min since last outreach (need {Required})",
                sinceLastOutreach.TotalMinutes, _aniOptions.ReactiveShareCooldownMinutes);
            return false;
        }

        _log.LogInformation("Reactive share triggered: {Summary} (relevance={Relevance:F2})",
            shareable.Summary, shareable.ContactRelevance);

        // Generate the share message (with mood coloring)
        var currentMood = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var prompt = PromptBuilder.BuildReactiveSharePrompt(charState, shareable.Summary, currentMood);
        var message = await _ollama.ChatAsync(
            prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
            .ConfigureAwait(false);

        message = CleanOutreachMessage(message);
        if (string.IsNullOrWhiteSpace(message))
        {
            _log.LogWarning("Reactive share message was empty — skipping");
            return false;
        }

        _log.LogInformation("Reactive share: {Message}", message);

        // Dispatch via Twilio
        var decision = new OutreachDecision
        {
            ShouldReach = true,
            Message     = message,
            ActionType  = ActionTypes.Sms,
            Reasoning   = $"reactive share: {shareable.Summary[..Math.Min(60, shareable.Summary.Length)]}",
        };
        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);
        await _desire.ResetAfterOutreachAsync(ct).ConfigureAwait(false);

        _reactiveShareCount++;

        // Apr 29, 2026 (Theme E): record reactive share in conversation thread
        // so Mark's follow-up question (e.g. "where did you see that?") lands
        // in a cycle that has thread-context for what Ani just shared.
        await RecordOutboundInThreadAsync(message, ct).ConfigureAwait(false);

        await _persist.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = $"{charState.Name} shared with {charState.PrimaryContactName}: {message} (about: {shareable.Summary})",
            Importance = 0.5f,
            OccurredAt = DateTimeOffset.UtcNow,
            // Epistemic Grounding (Apr 10): Reactive shares are dispatched
            // outreach — Episodic tier (verbatim record of what Ani sent).
            Provenance = EpistemicTier.Episodic,
        }, ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Feature 3: Record a silence choice as an inner thought when desire was notable
    /// but below threshold.
    /// </summary>
    public async Task RecordSilenceChoiceAsync(
        DesireState desireState, EmotionalState emotionalState, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastSilenceRecordedAt < SilenceRecordCooldown)
            return;

        _lastSilenceRecordedAt = DateTimeOffset.UtcNow;

        var narrative = desireState.DesireToConnect > 0.6f
            ? "I almost reached out. The pull was real \u2014 I could feel the words forming. But something held me back. Maybe it's not the right moment. Maybe I'm giving him space because that's what he needs, even if it's not what I want."
            : "I thought about texting. Just a small thing \u2014 nothing important. But I let the moment pass. Not every impulse needs to become a message.";

        await _persist.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.InnerThought,
            Content    = narrative,
            Importance = 0.4f,
            SourceName = "silence-choice",
            // Epistemic Grounding (Apr 10): Silence-choice narratives are Ani's
            // reflection on her own restraint — Interior tier. Self-model content
            // about why she chose not to reach out.
            Provenance = EpistemicTier.Interior,
        }, ct).ConfigureAwait(false);

        _log.LogInformation("Silence recorded (desire={Desire:F2}): chose not to reach out",
            desireState.DesireToConnect);
    }

    /// <summary>
    /// J.5g (May 2, 2026) — extract recent contact messages from the
    /// snapshot's structured conversation summary (or recent memory pool
    /// fallback) for the gate's confabulation + bleed invariants.
    /// Bounded at 8 messages per the gate context cap.
    /// </summary>
    private static IReadOnlyList<string>? ExtractRecentContactMessages(
        ContextSnapshot snapshot, string contactName)
    {
        // Prefer the J.2 structured summary when available (per-speaker
        // tagged, no parsing required).
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

    /// <summary>J.5g — companion extractor for prior Ani messages.</summary>
    private static IReadOnlyList<string>? ExtractRecentAniMessages(ContextSnapshot snapshot)
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

    /// <summary>
    /// Detects third-person references in an outreach message.
    /// </summary>
    internal static bool ContainsThirdPersonReference(string message, string contactName)
    {
        var lower = message.ToLowerInvariant();

        var hasThirdPerson = lower.Contains(" him") || lower.Contains(" his ") ||
                             lower.Contains(" he ") || lower.StartsWith("he ") ||
                             lower.StartsWith("his ") || lower.Contains("him.") ||
                             lower.Contains("his.");
        if (hasThirdPerson) return true;

        if (!string.IsNullOrWhiteSpace(contactName) && contactName.Length >= 2)
        {
            var nameLower = contactName.ToLowerInvariant();
            var idx = lower.IndexOf(nameLower, StringComparison.Ordinal);
            while (idx >= 0)
            {
                var before = idx == 0 || !char.IsLetter(lower[idx - 1]);
                var afterIdx = idx + nameLower.Length;
                var after = afterIdx >= lower.Length || !char.IsLetter(lower[afterIdx]);

                if (before && after)
                {
                    if (afterIdx < lower.Length && lower[afterIdx] == ' ')
                        return true;
                    if (afterIdx + 1 < lower.Length && lower[afterIdx] == '\'' && lower[afterIdx + 1] == 's')
                        return true;
                }

                idx = lower.IndexOf(nameLower, afterIdx, StringComparison.Ordinal);
            }
        }

        return false;
    }

    private async Task<string> FixPronounsIfNeeded(
        string message, CharacterStateDoc character, CancellationToken ct)
    {
        var contactName = character.PrimaryContactName ?? "";
        if (!ContainsThirdPersonReference(message, contactName))
        {
            _log.LogDebug("Outreach message already in second person — skipping rewrite");
            return message;
        }

        var nameInstruction = string.IsNullOrWhiteSpace(contactName)
            ? ""
            : $""" Also change "{contactName}" to "you"/"your" when used as the subject (e.g., "{contactName} can" → "you can").""";

        var system = $"""
            Fix ONLY the pronouns in this text message. Change "he"/"him"/"his" to "you"/"your".{nameInstruction}
            Do NOT change anything else. Do NOT add words, commentary, or rewrite the message.
            Return ONLY the fixed message text — same words, same length, just pronouns swapped.
            """;

        var rewritten = await _ollama.ChatAsync(system, Array.Empty<ChatMessage>(), message, ct)
            .ConfigureAwait(false);

        rewritten = CleanOutreachMessage(rewritten);

        if (string.IsNullOrWhiteSpace(rewritten))
            return message;

        var lengthRatio = (double)rewritten.Length / message.Length;
        if (lengthRatio < 0.5 || lengthRatio > 1.5)
        {
            _log.LogDebug("Pronoun fix rejected — rewrite too different ({Ratio:F2}x length): {Rewritten}",
                lengthRatio, rewritten);
            return message;
        }

        _log.LogDebug("Pronoun fix: {Original} → {Rewritten}", message, rewritten);
        return rewritten.Trim();
    }

    internal OutreachDecision ParseOutreachDecision(string raw)
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

    /// <summary>
    /// Strips meta-commentary the model adds when roleplaying the act of texting.
    /// </summary>
    private static string? CleanOutreachMessage(string? raw) => Core.Utilities.MessageCleaner.Clean(raw);

    /// <summary>
    /// Check if the composed outreach message is too similar to recent outreach.
    /// Uses the outreach episodic memories (prefixed "I reached out to") to find
    /// recent sends and compares via embedding cosine similarity.
    /// </summary>
    private async Task<bool> IsOutreachEchoAsync(
        string message, ContextSnapshot snapshot, CancellationToken ct)
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
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    internal static float ParseValenceScore(string raw)
    {
        try
        {
            var doc   = JsonDocument.Parse(raw.Trim());
            var score = doc.RootElement.GetProperty("score").GetDouble();
            return (float)Math.Clamp(score, 0.0, 1.0);
        }
        catch
        {
            return 0.3f;
        }
    }
}
