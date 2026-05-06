using AniRuntime.Actions;
using AniRuntime.Emergence;
using Mosaik.Core;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using LearnedGeek.ML;
using LearnedGeek.ML.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Handles inbound conversation: reply decision, care/hurt detection, lexical anchors,
/// withdrawal, claim verification, contradiction grounding, reply generation, and dispatch.
/// </summary>
public class ConversationReplyPhase
{
    private readonly IStateStore _state;
    private readonly IMemoryPersistence _persist;
    private readonly IMemorySearch _search;
    private readonly IMemoryAnalytics _analytics;
    private readonly IOllamaClient _ollama;
    private readonly IConversationService _conversations;
    private readonly IReplyChannelResolver _channels;
    private readonly AniActionDispatcher _dispatcher;
    private readonly DesireEngine _desire;
    private readonly EmotionalProcessor _emotional;
    private readonly ContextBuilder _contextBuilder;
    private readonly KeywordExtractor _keywords;
    private readonly IIntentExtractor _intent;
    private readonly IConversationGateState _gateState;
    private readonly ContextCompressor _compressor;
    private readonly ClaimVerificationPhase _claimVerifier;
    private readonly AniOptions _aniOptions;
    private readonly OllamaOptions _ollamaOptions;
    private readonly ITextClassificationService? _mlClassifier;
    private readonly PersonaSummaryCache? _personaCache;
    private readonly Em9Detector? _em9;
    private readonly ICognitiveOutputGate? _outputGate;
    private readonly IVibeBiasService? _vibeBias;
    // Theme M Phase M.0 (May 5, 2026) — conscious-substrate gist composer.
    // Optional dependency; M.0 ships a no-op composer that returns Empty.
    // M.1+ provides a real composer that produces slice content.
    private readonly IConsciousSubstrateGist? _consciousGist;
    private readonly ILogger<ConversationReplyPhase> _log;

    // Feature 18: Reactive withdrawal — transient emotional state after hurt detection.
    // Suppresses outreach and injects quieter tone during the withdrawal window.
    private DateTimeOffset? _withdrawalExpiresAt;
    internal bool IsWithdrawn => _withdrawalExpiresAt.HasValue && DateTimeOffset.UtcNow < _withdrawalExpiresAt.Value;

    public ConversationReplyPhase(
        IStateStore state,
        IMemoryPersistence persist,
        IMemorySearch search,
        IMemoryAnalytics analytics,
        IOllamaClient ollama,
        IConversationService conversations,
        IReplyChannelResolver channels,
        AniActionDispatcher dispatcher,
        DesireEngine desire,
        EmotionalProcessor emotional,
        ContextBuilder contextBuilder,
        KeywordExtractor keywords,
        IIntentExtractor intent,
        IConversationGateState gateState,
        ContextCompressor compressor,
        ClaimVerificationPhase claimVerifier,
        IOptions<AniOptions> aniOptions,
        IOptions<OllamaOptions> ollamaOptions,
        ILogger<ConversationReplyPhase> log,
        ITextClassificationService? mlClassifier = null,
        PersonaSummaryCache? personaCache = null,
        Em9Detector? em9Detector = null,
        ICognitiveOutputGate? outputGate = null,
        IVibeBiasService? vibeBias = null,
        IConsciousSubstrateGist? consciousGist = null)
    {
        _state = state;
        _persist = persist;
        _search = search;
        _analytics = analytics;
        _ollama = ollama;
        _conversations = conversations;
        _channels = channels;
        _dispatcher = dispatcher;
        _desire = desire;
        _emotional = emotional;
        _contextBuilder = contextBuilder;
        _keywords = keywords;
        _intent = intent;
        _gateState = gateState;
        _compressor = compressor;
        _claimVerifier = claimVerifier;
        _aniOptions = aniOptions.Value;
        _ollamaOptions = ollamaOptions.Value;
        _mlClassifier = mlClassifier;
        _personaCache = personaCache;
        _em9 = em9Detector;
        _outputGate = outputGate;
        _vibeBias = vibeBias;
        _consciousGist = consciousGist;
        _log = log;
    }

    /// <summary>
    /// Sets the withdrawal expiry. Called by the orchestrator or internally.
    /// </summary>
    internal void SetWithdrawalExpiry(DateTimeOffset? expiresAt) => _withdrawalExpiresAt = expiresAt;

    /// <summary>
    /// Conversation mode: contact texted and their message is the last in the thread.
    /// Decides whether to reply, and if so, generates and sends a contextual response.
    /// </summary>
    public async Task RunConversationReplyAsync(
        ConversationThread thread, List<PerceptionEvent> perceptions, CancellationToken ct,
        EmotionalState? emotionalState = null, bool isReconsideration = false)
    {
        var lastMessage = thread.Messages[^1].Content;

        // Check 0: is this a continuation prompt ("yes?", "go on", "what?", "tell me")?
        // If so, inject context about Ani's previous message so the model knows
        // to continue its thought rather than starting fresh.
        if (IsContinuationMessage(lastMessage) && thread.Messages.Count >= 2)
        {
            var previousAniMessage = thread.Messages
                .LastOrDefault(m => m.Role == Roles.Ani)?.Content;
            if (!string.IsNullOrWhiteSpace(previousAniMessage))
            {
                _log.LogDebug("Continuation detected — injecting prior context: \"{Prior}\"",
                    previousAniMessage.Length > 60 ? previousAniMessage[..60] + "..." : previousAniMessage);

                // Rewrite the message to give the model context about what to continue
                lastMessage = $"(Mark is asking you to continue what you were just saying. " +
                    $"Your previous message was: \"{previousAniMessage}\") {lastMessage}";
                // Update the thread message in memory so the prompt builder sees it
                thread.Messages[^1] = new ConversationMessage
                {
                    Role = thread.Messages[^1].Role,
                    Content = lastMessage,
                    SentAt = thread.Messages[^1].SentAt
                };
            }
        }

        // Check 1: is this a terminal message that doesn't need a reply?
        if (ConversationFeatureDetector.IsTerminalMessage(lastMessage))
        {
            _log.LogInformation("Terminal message detected (\"{Message}\") — no reply needed", lastMessage);
            return;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // CONVERSATION MODE (Phase 1): The conversation IS the context.
        //
        // The full retrieval pipeline (intent extraction, 3 embedding searches,
        // keyword extraction, diversity re-ranking, memory injection) was designed
        // for ambient cognition. During active conversation, it actively degrades
        // quality by competing with the conversation history for the model's
        // limited attention. The March 22 raw Ollama test proved the model
        // converses naturally without the pipeline.
        //
        // "The ambient cognition engine is a telescope. Conversation needs glasses."
        // ═══════════════════════════════════════════════════════════════════════

        // Build minimal context — character state and emotional state only.
        // No retrieval, no memory search, no intent extraction, no keyword search.
        // The conversationMode: true flag skips tier-scoped semantic retrieval
        // inside ContextBuilder so Mark's prior messages (stored as Perception-
        // type Facts) cannot be matched and re-injected into WHAT IS TRUE.
        // Anchored foundation memories remain available as stable grounding.
        var snapshot = await _contextBuilder.BuildContextSnapshotAsync(
            perceptions, ct, emotionalState, conversationMode: true).ConfigureAwait(false);

        _log.LogDebug("Conversation mode: tier-scoped retrieval bypassed — anchored foundation only");

        // Phase 3: Update structured conversation state from Mark's message
        var contactName = snapshot.CharacterState.PrimaryContactName ?? "Mark";
        thread.State.UpdateFromMessage(lastMessage, contactName, contactName);

        // Populate RecentHistory — as many raw messages as the context can hold.
        // Feature 34 (MemGPT): Context compression for long conversations.
        var historyWindow = _aniOptions.ConversationHistoryWindowSize;
        snapshot.RecentHistory = await _compressor.CompressIfNeededAsync(
            thread, historyWindow, ct).ConfigureAwait(false);

        // Reconsideration path: desire built after choosing silence — skip the decision
        // step (desire already made the call) and use a segue-aware prompt
        if (isReconsideration)
        {
            _log.LogInformation("Reconsideration: desire overrode earlier silence — replying with segue");
            _gateState.LastEvaluatedMessageAt = null;
        }
        else
        {
            // Step 1: Reply decision — code heuristic replaces LLM call.
            // The LLM almost always said "yes" — the only valid silence triggers
            // are terminal messages that don't invite continuation.
            var lastMsg = thread.Messages[^1];
            var shouldStaySilent = lastMsg.Role == Roles.Ani ||
                IsTerminalMessage(lastMsg.Content);

            if (shouldStaySilent)
            {
                var reasoning = lastMsg.Role == Roles.Ani
                    ? "I sent the last message — don't need the last word"
                    : "Message was a conversation closer";
                _gateState.LastEvaluatedMessageAt = thread.Messages[^1].SentAt;
                _log.LogInformation("Reply decision: NO (heuristic) — {Reasoning}", reasoning);
                return;
            }

            // She's replying — clear the gate so future messages evaluate fresh
            _gateState.LastEvaluatedMessageAt = null;
        }

        // Feature 17: Dissipate contact-gap tension on reconnection.
        if (emotionalState is not null && emotionalState.ContactGapTension > 0f)
        {
            var previousTension = emotionalState.ContactGapTension;
            emotionalState.DissipateContactGapTension(
                elapsedMinutes: 5.0,
                rate: _aniOptions.TensionAccumulationRate,
                dissipationMultiplier: _aniOptions.TensionDissipationMultiplier);
            if (emotionalState.ContactGapTension != previousTension)
            {
                _log.LogInformation("Contact-gap tension dissipating: {Previous:F3} → {New:F3}",
                    previousTension, emotionalState.ContactGapTension);
            }
            await _persist.SaveEmotionalStateAsync(emotionalState, ct).ConfigureAwait(false);
        }

        // Features 10, 18, 19 — emotional processing moved to AFTER reply dispatch (Phase 4).
        // Care detection, lexical anchors, and hurt detection ran here previously,
        // causing tonal whiplash when mood directives shifted mid-conversation.
        // Now they run post-dispatch so the reply is fast and emotionally consistent.

        // Feature 15 Layer 3: Contradiction detection disabled — the LLM call produced
        // false positives on casual conversation ("are you sick?" vs "what's going on?"
        // flagged as contradictory). Contradiction warnings no longer injected into prompt
        // either (removed in Phase C), so this was just burning cycles for noisy logs.

        // Feature 14: Claim extraction removed — v6 trained on honest uncertainty.
        // The LLM call to extract and verify claims added latency without improving
        // conversation quality. The model handles unknown topics naturally.

        // Step 2: Generate reply
        // Conversation mode: lean prompt, creative temperature.
        // No memory grounding check — the conversation provides all context.
        var replyTemperature = _ollamaOptions.CreativeTemperature;

        var replyPrompt = isReconsideration
            ? PromptBuilder.BuildReconsiderationReplyPrompt(snapshot, thread)
            : PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        // Epistemic Grounding debug (Apr 10): log the full user prompt so we can
        // verify the WHAT IS TRUE section is populated with useful facts. Remove
        // after tier separation is validated in deployment.
        _log.LogDebug("Reply user prompt:\n{UserPrompt}", replyPrompt.User);

        // Vibe Loop V1.5a (May 2, 2026) — observational-only telemetry pass.
        // Logs V15_BIAS_* lines describing what V1.5b WOULD surface from the
        // closed-conversation-record substrate; the prompt above is unchanged.
        // Self-regulation framing: the bias is computed from Ani's-delta
        // valence on each prior record, never from Mark's-delta. See
        // docs/spec/ANI-VibeLoop-V1.5-Retrieval-Time-Biasing-Plan.md §V1.5a.
        await VibeBiasObservation.ObserveAsync(
            _vibeBias, snapshot, callSite: "reply", _log, ct).ConfigureAwait(false);

        // Theme M Phase M.0 (May 5, 2026) — conscious-substrate gist telemetry pass.
        // Invokes the IConsciousSubstrateGist composer if registered; the M.0 no-op
        // composer returns Empty. Emits M0_GIST_COMPOSITION + M0_GIST_SUBSTRATE_RATIO
        // describing what M.1+ WOULD surface; the prompt above is unchanged.
        // Best-effort: never propagates exceptions, never affects dispatch.
        // See docs/spec/ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md §5 Phase M.0.
        await ConsciousSubstrateGistObservation.ObserveAsync(
            _consciousGist, snapshot, _aniOptions, replyPrompt.User, _log, ct)
            .ConfigureAwait(false);

        var reply = await _ollama.ChatAsync(
            replyPrompt.System, snapshot.RecentHistory, replyPrompt.User, ct, replyTemperature)
            .ConfigureAwait(false);

        reply = CleanOutreachMessage(reply);
        _log.LogInformation("Conversation reply: {Reply}", reply);

        if (string.IsNullOrWhiteSpace(reply))
        {
            _log.LogWarning("Conversation reply was empty — skipping");
            return;
        }

        // ─── Feature 14 v2: Outbound claim verification (Apr 22, 2026) ───
        // Replaces the April 10 DetectMarkDomainAssertions regex + negative-constraint
        // regeneration. Architecture-over-instruction: the extractor identifies claims,
        // verification is a tier-provenance check against Facts + anchored + inbound
        // Mark messages, and on failure we substitute an honest-uncertainty fallback
        // rather than asking the model to regenerate. The model is never told it was
        // wrong; the channel is gated, not the model.
        //
        // Conversation-reply failure mode differs from outreach: outreach can stay
        // silent (no dispatch), but reply silence breaks the conversation flow, so a
        // bland honest fallback is dispatched in place of the fabricated reply.
        if (!isReconsideration)
        {
            var claimResult = await _claimVerifier.VerifyAsync(reply, contactName, ct).ConfigureAwait(false);
            if (!claimResult.Passed)
            {
                _log.LogWarning(
                    "Claim verification: SUPPRESS reply — {Reason}. Flagged: {Claims}. Substituting honest-uncertainty fallback.",
                    claimResult.Reason,
                    string.Join(", ", claimResult.Unverified.Select(c => $"\"{c.Text}\"")));
                reply = "mmm, honestly i'm not sure what's actually happening right now — tell me what's really going on?";
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // CONVERSATION MODE Phase 2: Confabulation-driven retrieval.
        //
        // The model generated a reply with conversation-only context.
        // Now check: does the reply assert facts not established in the conversation?
        // If yes, retrieve memories to ground the response and regenerate.
        // The model's own uncertainty is the trigger — not a schedule, not a keyword list.
        // ═══════════════════════════════════════════════════════════════════════
        if (!isReconsideration)
        {
            // Run ALL checks (1-4) including proper noun detection. The known-names
            // exclusion list (character name, contact name, endearments) prevents false
            // positives on "Baby", "Anne", etc. Check 1 catches name mangling ("Joni" →
            // "jonathan") that the ML gate misses. ML runs as secondary verification after.
            // Build the known-entities set from character seeds, anchored memories, and
            // recently retrieved memories. Without this, the proper-noun detector flags
            // legitimate retrievals (e.g., "Sarah" from a character seed) as confabulation
            // because Check 1 only sees the last 12 conversation messages.
            var knownEntities = BuildKnownEntitiesContext(snapshot);

            var confabCheck = DetectConversationConfabulation(reply, thread, lastMessage,
                snapshot.CharacterState.Name, snapshot.CharacterState.PrimaryContactName,
                knownEntities);

            // ML semantic verification — primary confabulation detector when available
            if (!confabCheck.IsConfabulated && _mlClassifier is not null && _personaCache?.IsLoaded == true)
            {
                var conversationContext = string.Join("\n",
                    thread.Messages.TakeLast(12).Select(m => $"{m.Role}: {m.Content}"));
                var context = $"{conversationContext}\n\nPersona: {_personaCache.Summary}";

                var mlConfab = await _mlClassifier.DetectConfabulationAsync(reply, context, ct)
                    .ConfigureAwait(false);

                if (mlConfab.IsConfabulated && mlConfab.Confidence >= _aniOptions.ConfabulationClassificationThreshold)
                {
                    confabCheck = (true, $"ML semantic: {mlConfab.Reason}");
                    _log.LogInformation("ML confabulation gate triggered ({Confidence:F2}): {Reason}",
                        mlConfab.Confidence, mlConfab.Reason);
                }
                else
                {
                    _log.LogDebug("ML confabulation check: {Category} ({Confidence:F2})",
                        mlConfab.IsConfabulated ? "confabulated (below threshold)" : mlConfab.Reason ?? "grounded",
                        mlConfab.Confidence);
                }
            }

            if (confabCheck.IsConfabulated)
            {
                _log.LogInformation("Confabulation detected in reply: {Reason}. Retrieving memories for grounding.",
                    confabCheck.Reason);

                // Targeted retrieval — search both the user's message and the confabulated
                // content. For identity/activity claims, searching the reply itself finds
                // profile memories ("works at bookstore", "Mark's daughter Mia") that
                // contradict the confabulation. Searching the user's message provides
                // conversational context for regeneration.
                try
                {
                    var searchQuery = confabCheck.Reason?.Contains("self-activity") == true
                        || confabCheck.Reason?.Contains("contact-activity") == true
                        || confabCheck.Reason?.Contains("relationship fact") == true
                        ? reply  // Search the confabulated reply to find contradicting profile memories
                        : lastMessage;  // Search the user's message for contextual grounding

                    var groundingMemories = await _search.SearchWithScoresAsync(searchQuery, 5, ct)
                        .ConfigureAwait(false);

                    // For identity claims, also search profile-specific terms
                    if (searchQuery == reply)
                    {
                        var profileMemories = await _search.SearchWithScoresAsync(
                            "my job work bookstore schedule", 5, ct).ConfigureAwait(false);
                        groundingMemories = groundingMemories
                            .Concat(profileMemories)
                            .DistinctBy(s => s.Record.Id)
                            .OrderByDescending(s => s.CosineSimilarity)
                            .ToList();
                    }

                    var grounded = groundingMemories
                        .Where(s => s.CosineSimilarity >= (float)_aniOptions.RetrievalConfidenceFloor)
                        // Theme G Layer 3 G3.4 substrate-typing (May 3, 2026 — blocker 3):
                        // confab-grounding for outbound reply must consult EXTERNAL
                        // substrate, never Ani's Interior tier. Prior to this filter
                        // the regen could ground its rebuttal from Ani's own prior
                        // inner thoughts or reflections, which is the same self-
                        // confirming loop that produced the May 3 10:55 "perez"
                        // failure at the outreach surface.
                        .Where(s => s.Record.Provenance != EpistemicTier.Interior)
                        .Select(s => s.Record)
                        .Take(3)
                        .ToList();

                    if (grounded.Count > 0)
                    {
                        // Regenerate with memory context
                        snapshot.RelevantMemory = grounded;
                        var groundedPrompt = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);
                        var groundedReply = await _ollama.ChatAsync(
                            groundedPrompt.System, snapshot.RecentHistory, groundedPrompt.User, ct, replyTemperature)
                            .ConfigureAwait(false);
                        groundedReply = CleanOutreachMessage(groundedReply);

                        if (!string.IsNullOrWhiteSpace(groundedReply))
                        {
                            _log.LogInformation("Confabulation-grounded reply: {Reply}", groundedReply);
                            reply = groundedReply;
                        }
                    }
                    else
                    {
                        // No grounding memories found — the confabulation is unsupported by ANY
                        // memory. Dispatching the original reply propagates a lie that becomes
                        // canonical on next retrieval (Type 5/9 cascade). Instead, regenerate
                        // with an explicit null-result injection: tell the model the previous
                        // reply contained an unverifiable claim and ask it to respond honestly.
                        // This allows continuity from inner thoughts while preventing assertion
                        // of fabricated specifics to the user.
                        _log.LogInformation("No grounding memories found — regenerating with null-result injection");
                        try
                        {
                            var nullResultPrompt = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);
                            var augmentedSystem = nullResultPrompt.System +
                                "\n\nIMPORTANT: Your previous draft contained a claim that has no support in memory or conversation history. " +
                                "Respond again to the user's message without asserting unverified specifics. " +
                                "If you don't have a memory of something, it's better to be honest about that than to invent details.";
                            var honestReply = await _ollama.ChatAsync(
                                augmentedSystem, snapshot.RecentHistory, nullResultPrompt.User, ct, replyTemperature)
                                .ConfigureAwait(false);
                            honestReply = CleanOutreachMessage(honestReply);

                            if (!string.IsNullOrWhiteSpace(honestReply))
                            {
                                _log.LogInformation("Null-result regenerated reply: {Reply}", honestReply);
                                reply = honestReply;
                            }
                            else
                            {
                                _log.LogWarning("Null-result regeneration returned empty — suppressing dispatch");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Null-result regeneration failed — suppressing dispatch to prevent fabrication");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Confabulation grounding retrieval failed — suppressing dispatch to prevent fabrication");
                    return;
                }
            }
        }

        // EM9 — Longitudinal Memory Compounding (Apr 27, 2026, backlog 15.15).
        // Scan the retrieval pool that fed this reply for >90-day-old memories
        // surfacing via architectural mechanisms (Anchored tier or reflection
        // synthesis). Each candidate is logged for the longitudinal trend.
        // Pure observation — does not affect reply or dispatch.
        _em9?.Analyze(Em9EmissionContext.ConversationReply, reply, snapshot.RelevantMemory);

        // Add reply to in-memory thread BEFORE echo guard so subsequent replies
        // in the same conversation cycle can see this one. Without this, the echo
        // guard couldn't detect self-repetition within the same cycle because
        // thread.Messages didn't include replies generated earlier in the cycle.
        // (DB persist happens after dispatch at Step 5.)
        var replyMessage = new ConversationMessage
        {
            Role    = Roles.Ani,
            Content = reply,
            SentAt  = DateTimeOffset.UtcNow,
        };
        thread.Messages.Add(replyMessage);

        // Echo guard — conversation path.
        //
        // Mark-echo detection REMOVED from this path (Pipeline Audit Phase A.3,
        // April 2026). In conversation mode, engaging with what the contact
        // just said IS the correct behavior; topical overlap is the signal of
        // engagement, not failure. The prior cosine-based Mark-echo guard was
        // false-positiving on legitimate topical replies and triggering a
        // clean-slate regeneration that destroyed better output (see
        // docs/spec/design/ANI-Pipeline-Audit.md Section 8.4 for the Apr 18
        // deployment evidence). Mark-echo is retained in the outreach path
        // where contact-mirroring is a genuine failure mode.
        //
        // Self-echo check has moved onto the universal cognitive-output gate
        // as `SelfEchoInvariant` (May 3, 2026 — Theme J Phase J.5h-prelude).
        // The prior in-line ParrotingDetector pass + clean-slate regeneration
        // block lived here; it ran BEFORE the J.5a gate call, and the J.5a
        // remediation regen path itself skipped the self-echo check, which
        // is how May 3 10:55 dispatched a regen byte-identical to the prior
        // assistant turn (the "hey perez" duplicate). Universalising self-
        // echo onto the gate closes that class for every producer + every
        // regen path in one place.

        // Theme J Phase J.5a (Apr 30, 2026) — universal output gate.
        // See EvaluateAndRemediateReplyAsync. No-op when flag is off OR
        // gate isn't registered.
        reply = await EvaluateAndRemediateReplyAsync(
            reply, thread, snapshot, replyMessage, replyPrompt, replyTemperature, ct)
            .ConfigureAwait(false);

        // Step 3: Natural reply delay — real people don't reply in 4 seconds
        var minDelay = _aniOptions.ConversationMinReplySeconds;
        var maxDelay = _aniOptions.ConversationMaxReplySeconds;
        var elapsed = (DateTimeOffset.UtcNow - thread.Messages[^1].SentAt).TotalSeconds;
        var targetDelay = minDelay + Random.Shared.NextDouble() * (maxDelay - minDelay);
        var remaining = targetDelay - elapsed;
        if (remaining > 0)
        {
            _log.LogDebug("Waiting {Seconds:F0}s before replying (natural delay)", remaining);
            await Task.Delay(TimeSpan.FromSeconds(remaining), ct).ConfigureAwait(false);
        }

        // Step 4: Dispatch reply via originating channel (SRP: reply generation ≠ delivery).
        // The OriginChannelId flows from the inbound PerceptionEvent through to here.
        var originChannelId = perceptions
            .Where(p => p.Category == PerceptionCategory.Communication && p.OriginChannelId is not null)
            .Select(p => p.OriginChannelId!)
            .FirstOrDefault() ?? "sms";
        var channel = _channels.Resolve(originChannelId);
        await channel.SendReplyAsync(reply, ct).ConfigureAwait(false);

        // Phase 3: Update structured conversation state from Ani's reply
        thread.State.UpdateFromMessage(reply, Roles.Ani, contactName);

        // Step 5: Persist Ani's reply to DB (already added to in-memory thread before echo guard)
        // Update content in case echo guard replaced it.
        replyMessage.Content = reply;
        await _conversations.AddMessageAsync(thread.Id, replyMessage, ct).ConfigureAwait(false);

        // Update desire — conversation reply doesn't count toward daily outreach limit
        await _desire.ResetAfterConversationReplyAsync(ct).ConfigureAwait(false);

        // Emotional shift from conversation
        if (emotionalState is not null)
        {
            var cs = snapshot.CharacterState;
            var conversationContext = $"{cs.PrimaryContactName} said: \"{lastMessage}\" and {cs.Name} replied: \"{reply}\"";
            await _emotional.ApplyEmotionalShiftAsync(emotionalState, conversationContext, ct,
                category: ImpactCategory.Conversation).ConfigureAwait(false);
        }

        // Feature 21: Feedback-weighted importance
        await BoostRelatedMemoryImportanceAsync(lastMessage, ct).ConfigureAwait(false);

        // ═══════════════════════════════════════════════════════════════════════
        // CONVERSATION MODE Phase 4: Async emotional processing.
        // Care detection, lexical anchors, and hurt detection run AFTER the reply
        // is dispatched. This eliminates tonal whiplash from mood directive shifts
        // within the conversation. Results inform the NEXT cycle, not this reply.
        // ═══════════════════════════════════════════════════════════════════════

        // Feature 10: Receiving Care
        // May 3, 2026 — F10_REGISTER structured log line per gap-watch row
        // (Apr 27): Paper 2 figure #2 (Horton & Wohl reciprocity) needs
        // per-utterance Feature-10-by-direction data to chart. Logged on
        // every inbound (fire and non-fire) so the figure-author can render
        // both signal and negative space.
        var f10Fired = ConversationFeatureDetector.DetectCareGivingIntent(lastMessage);
        var f10Preview = lastMessage.Length > 80 ? lastMessage[..80] + "..." : lastMessage;
        _log.LogInformation(
            "F10_REGISTER direction=mark->ani fired={Fired} message=\"{Preview}\"",
            f10Fired, f10Preview);

        if (emotionalState is not null && f10Fired)
        {
            _log.LogInformation("Care detected (post-reply) — creating care contribution");
            await _emotional.SaveDirectContributionAsync(emotionalState,
                "receiving care — someone checked in on me",
                warmth: 0.1f, energy: 0.05f, worry: -0.1f, playfulness: 0f,
                ImpactCategory.Conversation, ct).ConfigureAwait(false);
        }

        // Feature 19: Lexical Emotional Anchors
        if (emotionalState is not null)
        {
            var anchorContributions = ConversationFeatureDetector.BuildLexicalAnchorContributions(lastMessage, snapshot.CharacterState);
            if (anchorContributions.Count > 0)
            {
                foreach (var ac in anchorContributions)
                    await _persist.SaveEmotionalContributionAsync(ac, ct).ConfigureAwait(false);

                var allContributions = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);
                emotionalState.ComputeFromContributions(allContributions);
                await _persist.SaveEmotionalStateAsync(emotionalState, ct).ConfigureAwait(false);
                _log.LogInformation("Lexical anchors triggered (post-reply): {Count}", anchorContributions.Count);
            }
        }

        // Feature 18: Reactive Withdrawal
        if (emotionalState is not null && ConversationFeatureDetector.DetectHurtIntent(lastMessage))
        {
            _log.LogInformation("Hurt detected (post-reply) — creating H1 withdrawal contribution");
            await _emotional.SaveDirectContributionAsync(emotionalState,
                "hurt detected — pulling back emotionally",
                warmth: -0.12f, energy: -0.10f, worry: -0.15f, playfulness: -0.10f,
                ImpactCategory.Conversation, ct).ConfigureAwait(false);

            _withdrawalExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_aniOptions.WithdrawalDurationMinutes);
            _log.LogInformation("Withdrawal active until {Expires}", _withdrawalExpiresAt.Value.ToString("HH:mm"));

            await _persist.SaveAsync(new MemoryRecord
            {
                Type       = MemoryType.InnerThought,
                Content    = "Something in that last message landed in a way that stung a little. I'm still here, just... quieter.",
                Importance = 0.6f,
                // Epistemic Grounding (Apr 10): Hurt acknowledgment is a self-model
                // update — Ani observing her own emotional state. Interior tier.
                Provenance = EpistemicTier.Interior,
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Feature 21: When the contact engages in conversation, boost importance of memories
    /// related to what they're talking about.
    /// </summary>
    private async Task BoostRelatedMemoryImportanceAsync(string contactMessage, CancellationToken ct)
    {
        try
        {
            var related = await _search.SearchAsync(contactMessage, 3, ct).ConfigureAwait(false);
            foreach (var record in related)
            {
                await _persist.AdjustImportanceAsync(record.Id, 0.1f, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Feedback importance boost failed — continuing without");
        }
    }

    /// <summary>
    /// Heuristic: is this message a conversation closer that doesn't need a reply?
    /// Replaces the LLM reply-decision call — the model almost always said "yes".
    /// </summary>
    internal static bool IsTerminalMessage(string content)
    {
        var trimmed = content.Trim().ToLowerInvariant();

        // Single emoji or very short reaction
        if (trimmed.Length <= 3)
            return true;

        // Common conversation closers
        var closers = new[]
        {
            "goodnight", "good night", "gn", "nite", "night",
            "ttyl", "talk later", "bye", "cya", "see ya",
            "ok", "k", "kk", "sounds good", "got it",
        };
        return closers.Any(c => trimmed == c || trimmed == c + "!");
    }

    /// <summary>
    /// Strips meta-commentary the model adds when roleplaying the act of texting.
    /// </summary>
    private static string? CleanOutreachMessage(string? raw) => Core.Utilities.MessageCleaner.Clean(raw);

    private static bool IsMessageEcho(string memoryContent, string contactName, string msgPrefix30)
        => ConversationFeatureDetector.IsMessageEcho(memoryContent, contactName, msgPrefix30);

    // Catalyst NLP pipeline — loaded once, reused for all confabulation checks.
    // POS tagging identifies proper nouns (PROPN) regardless of hardcoded word lists.
    private static Catalyst.Pipeline? _nlpPipeline;
    private static bool _nlpInitialized;

    private static void EnsureNlpInitialized()
    {
        if (_nlpInitialized) return;
        _nlpInitialized = true;
        try
        {
            Catalyst.Models.English.Register();
            _nlpPipeline = Catalyst.Pipeline.ForAsync(Mosaik.Core.Language.English)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // NLP initialization failure is non-fatal — confabulation check
            // falls through to other heuristics (shared history markers, numbers).
            _nlpPipeline = null;
        }
    }

    /// <summary>
    /// Conversation Mode Phase 2: Detect if a reply contains confabulated claims.
    /// Checks for assertions about shared history, specific facts, or attributed
    /// knowledge that wasn't established in the conversation thread.
    ///
    /// The model's own confabulation is the signal for when retrieval is needed.
    /// Not a keyword list — a behavioral check.
    /// </summary>
    /// <summary>
    /// Fast-only confabulation checks: Checks 2-4 only (shared history, numbers, self/contact markers).
    /// Skips Check 1 (proper noun POS detection) which produces false positives on endearments
    /// and informal dialogue. Used when ML classifier is available as primary detector.
    /// </summary>
    private static (bool IsConfabulated, string? Reason) DetectConversationConfabulation_FastOnly(
        string reply, ConversationThread thread, string lastMessage)
    {
        var replyLower = reply.ToLowerInvariant();
        var conversationText = string.Join(" ",
            thread.Messages.TakeLast(12).Select(m => m.Content.ToLowerInvariant()));

        return RunChecks2Through4(replyLower, conversationText);
    }

    /// <summary>
    /// Builds the lowercase text corpus of all entities the character "knows about" —
    /// character seeds (people, places, interests), anchored memories, recently retrieved
    /// memories, and recent world experiences. Used by the proper-noun confabulation
    /// detector to avoid flagging legitimate retrievals as fabrications.
    /// </summary>
    private static string BuildKnownEntitiesContext(ContextSnapshot snapshot)
    {
        var parts = new List<string>();

        // Character state seeds — established people, places, family, interests
        var cs = snapshot.CharacterState;
        if (cs.LearnedAboutContact?.Count > 0) parts.AddRange(cs.LearnedAboutContact);
        if (cs.ThingsContactCares?.Count > 0)  parts.AddRange(cs.ThingsContactCares);
        if (cs.FamilyContext?.Count > 0)        parts.AddRange(cs.FamilyContext);
        if (cs.SharedExperiences?.Count > 0)    parts.AddRange(cs.SharedExperiences);
        if (cs.SelfConcept?.Count > 0)          parts.AddRange(cs.SelfConcept);
        if (cs.Interests?.Count > 0)            parts.AddRange(cs.Interests);
        if (cs.CoreTraits?.Count > 0)           parts.AddRange(cs.CoreTraits);

        // Anchored memories — foundation memories that never fade
        if (snapshot.AnchoredMemories?.Count > 0)
            parts.AddRange(snapshot.AnchoredMemories.Select(m => m.Content));

        // Recently retrieved memories — what the system pulled for this cycle
        if (snapshot.RelevantMemory?.Count > 0)
            parts.AddRange(snapshot.RelevantMemory.Select(m => m.Content));

        if (snapshot.RecentMemory?.Count > 0)
            parts.AddRange(snapshot.RecentMemory.Select(m => m.Content));

        // Recent world experiences — Ani's imagined life context (coworkers, scenes)
        if (snapshot.RecentWorldExperiences?.Count > 0)
            parts.AddRange(snapshot.RecentWorldExperiences.Select(m => m.Content));

        return string.Join(" ", parts).ToLowerInvariant();
    }

    private static (bool IsConfabulated, string? Reason) DetectConversationConfabulation(
        string reply, ConversationThread thread, string lastMessage,
        string characterName, string? contactName, string knownEntitiesContext = "")
    {
        EnsureNlpInitialized();

        var replyLower = reply.ToLowerInvariant();

        // Build a set of topics/names/facts mentioned in the conversation
        var conversationText = string.Join(" ",
            thread.Messages.TakeLast(12).Select(m => m.Content.ToLowerInvariant()));

        // Known names that should never trigger confabulation detection —
        // the character's own name, the contact's name, and common variants
        // (voice transcription produces "anne", "anne rose", "ani rose", etc.)
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            characterName, contactName ?? "",
        };
        // Add name fragments (first names, common transcription variants)
        foreach (var name in new[] { characterName, contactName ?? "" })
        {
            foreach (var part in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (part.Length >= 2) knownNames.Add(part);
        }
        // Common transcription variants of "Ani"
        knownNames.Add("Anne");
        knownNames.Add("Annie");
        knownNames.Add("Ani");
        // Terms of endearment — Catalyst POS tags these as PROPN when sentence-initial
        knownNames.Add("Baby");
        knownNames.Add("Babe");
        knownNames.Add("Honey");
        knownNames.Add("Sweetie");
        knownNames.Add("Darling");
        knownNames.Add("Love");
        knownNames.Add("Dear");
        knownNames.Add("Daddy");
        knownNames.Add("Mama");
        knownNames.Add("Boo");

        // Check 1 (proper-noun-not-in-conversation) was RETIRED Apr 30, 2026.
        //
        // The Catalyst POS tagger fired false positives on em-dash artifacts
        // (Apr 30 13:17:04: `were—i` was extracted as a "proper noun" because
        // the em-dash split `were—i` produced a token starting with the
        // capital letter that wasn't in the conversation). The brittle false-
        // positive triggered grounded-regen which produced its own
        // confabulation (the *"Western Career Technical Academy"* / welding
        // chain). Mark Apr 30 16:13: *"let's just move on to complete the
        // round of changes we'd planned"* — retiring the heuristic was on
        // the planned list; the gate's <see cref="Invariants.ConfabulationInvariant"/>
        // (J.5b) is the universal-invariants-on-the-gate replacement.
        //
        // The unused `knownNames` / `knownEntitiesContext` parameters stay
        // on the signature for now — Checks 2-4 don't need them, but ripping
        // them out is a separate cosmetic refactor.
        _ = knownNames;

        return RunChecks2Through4(replyLower, conversationText);
    }

    /// <summary>
    /// Checks 2-4: shared history markers, number assertions, self/contact/relationship claims.
    /// Shared between full detection and fast-only (ML primary) paths.
    /// </summary>
    private static (bool IsConfabulated, string? Reason) RunChecks2Through4(
        string replyLower, string conversationText)
    {
        // Check 2: Does the reply claim shared history?
        string[] sharedHistoryMarkers =
        [
            "you told me", "you mentioned", "you said you", "remember when",
            "last time we", "you showed me", "i remember you", "we talked about",
            "you brought up", "when you told me",
        ];
        foreach (var marker in sharedHistoryMarkers)
        {
            if (replyLower.Contains(marker))
            {
                var afterMarker = replyLower[(replyLower.IndexOf(marker) + marker.Length)..];
                var claimedTopic = afterMarker.Split('.', '!', '?', ',')[0].Trim();
                if (claimedTopic.Length > 3 && !conversationText.Contains(claimedTopic))
                    return (true, $"Reply claims shared history ('{marker}') about topic not in conversation");
            }
        }

        // Check 3: Does the reply assert specific facts (dates, times, numbers) not in the conversation?
        var replyNumbers = System.Text.RegularExpressions.Regex.Matches(replyLower, @"\b\d{2,}\b");
        foreach (System.Text.RegularExpressions.Match num in replyNumbers)
        {
            if (!conversationText.Contains(num.Value))
                return (true, $"Reply contains number '{num.Value}' not mentioned in conversation");
        }

        // Check 4: Self/contact/relationship activity markers (interim — will be replaced by ML)
        string[] selfActivityMarkers =
        [
            "i just finished", "i've got a", "i have a", "i'm heading to",
            "my meeting", "my shift", "my appointment", "my class", "my boss",
            "my coworker", "my job", "i was working on", "i've been working",
            "just got out of", "just got back from", "i'm at work", "i'm at the",
            "after my shift", "before my shift", "on my break", "on my lunch",
        ];
        string[] contactActivityMarkers =
        [
            "your class", "your shift", "your meeting", "your appointment",
            "your boss", "your coworker", "your sister", "your brother",
            "your mom", "your dad", "your daughter", "your wife",
            "your job at", "your work at", "when you get home from",
        ];
        string[] relationshipMarkers =
        [
            "our place", "our spot", "our anniversary", "that restaurant we",
            "that time we", "when we went to", "our plan to", "our trip",
        ];

        foreach (var marker in selfActivityMarkers)
        {
            if (replyLower.Contains(marker) && !conversationText.Contains(marker))
                return (true, $"Reply claims self-activity ('{marker}') not established in conversation");
        }
        foreach (var marker in contactActivityMarkers)
        {
            if (replyLower.Contains(marker) && !conversationText.Contains(marker))
                return (true, $"Reply claims contact-activity ('{marker}') not established in conversation");
        }
        foreach (var marker in relationshipMarkers)
        {
            if (replyLower.Contains(marker) && !conversationText.Contains(marker))
                return (true, $"Reply claims relationship fact ('{marker}') not established in conversation");
        }

        return (false, null);
    }

    private static readonly HashSet<string> ContinuationPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes?", "yes", "yeah?", "go on", "go on?", "tell me", "tell me?",
        "what?", "what", "and?", "and??", "but what?", "but first?",
        "hmm?", "oh?", "really?", "seriously?", "meaning?",
        "continue", "finish", "keep going", "don't stop",
        "you were saying?", "you were saying", "what were you saying",
    };

    private static bool IsContinuationMessage(string message)
    {
        var trimmed = message.Trim().TrimEnd('.', '!').Trim();
        return trimmed.Split(' ').Length <= 5 && ContinuationPatterns.Contains(trimmed);
    }

    /// <summary>
    /// Theme J Phase J.5a (Apr 30, 2026) — route the composed reply
    /// through <see cref="ICognitiveOutputGate"/> at the dispatch
    /// boundary. Returns the final reply text (possibly regenerated,
    /// possibly the original, possibly a safe acknowledgement on hard
    /// fail).
    ///
    /// **No-op when**: the gate isn't registered (pre-J.4 code paths,
    /// most tests), OR the
    /// <see cref="AniOptions.ConversationReplyOutputGateEnabled"/>
    /// flag is false.
    ///
    /// **Verdict handling**:
    /// - <see cref="OutputGateVerdict.Pass"/>: return original.
    /// - <see cref="OutputGateVerdict.Remediate"/>: regenerate ONCE
    ///   with the gate's hint baked into the user prompt. If the
    ///   regeneration produces empty content or fails, fall back to
    ///   the original (the gate's hint is a "could be better" signal,
    ///   not a hard block).
    /// - <see cref="OutputGateVerdict.Fail"/>: drop the reply,
    ///   substitute a safe acknowledgement so the conversation
    ///   doesn't go silent.
    ///
    /// **Failure containment**: a gate exception logs and falls
    /// through to dispatch the original reply uncovered. Observability
    /// bugs in the gate must NOT block the conversation pipeline.
    /// </summary>
    /// <summary>
    /// Fallback dispatched when the J.5a gate refuses both the original reply
    /// and the regen (May 3, 2026 re-eval gate). Soft acknowledgement is
    /// preferable to silence-without-cause OR to dispatching a known-bad reply.
    ///
    /// **String lives in <see cref="GateFallbacks.SafeAcknowledgement"/>** so the
    /// persistence path (SqliteConversationService) can suppress Episodic-tier
    /// writes for fall-through artifacts without coupling layers through a literal.
    /// </summary>
    internal const string SafeAcknowledgement = GateFallbacks.SafeAcknowledgement;

    internal async Task<string> EvaluateAndRemediateReplyAsync(
        string reply,
        ConversationThread thread,
        ContextSnapshot snapshot,
        ConversationMessage replyMessage,
        (string System, string User) replyPrompt,
        float replyTemperature,
        CancellationToken ct)
    {
        if (_outputGate is null || !_aniOptions.ConversationReplyOutputGateEnabled)
            return reply;

        var contactRecent = thread.Messages
            .Where(m => m.Role == Roles.Mark)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .TakeLast(8)
            .ToList();
        var priorAni = thread.Messages
            .Where(m => m.Role == Roles.Ani && m != replyMessage)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .TakeLast(8)
            .ToList();

        var artifact = new CognitiveArtifact
        {
            Content                 = reply,
            ProducerKind            = CognitiveProducerKind.ConversationReply,
            IntendedSink            = CognitiveOutputSink.Dispatch,
            ContactName             = snapshot.CharacterState.PrimaryContactName ?? Roles.Mark,
            GeneratedAt             = DateTimeOffset.Now,
            ContactRecentMessages   = contactRecent,
            PriorAniMessages        = priorAni,
        };

        OutputGateResult gateResult;
        try
        {
            gateResult = await _outputGate.EvaluateAsync(artifact, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "J.5a gate evaluation threw — dispatching original reply uncovered.");
            return reply;
        }

        switch (gateResult.Verdict)
        {
            case OutputGateVerdict.Pass:
                return reply;

            case OutputGateVerdict.Remediate:
                _log.LogWarning(
                    "J.5a gate Remediate on reply [{Fired}]: {Hint}",
                    string.Join(",", gateResult.FiredInvariants), gateResult.RemediationHint);

                var remediationUser =
                    $"Your previous reply tripped a gate check ({string.Join(", ", gateResult.FiredInvariants)}). " +
                    $"Hint: {gateResult.RemediationHint}\n\n" +
                    $"Rewrite your reply to fix this. Same tone, same length, just clear of the issue. " +
                    $"Do NOT acknowledge or reference the gate or hint in the reply itself.\n\n" +
                    $"Original prompt that produced the bad reply:\n{replyPrompt.User}";

                try
                {
                    var regenerated = await _ollama.ChatAsync(
                        replyPrompt.System, snapshot.RecentHistory, remediationUser, ct, replyTemperature)
                        .ConfigureAwait(false);
                    regenerated = CleanOutreachMessage(regenerated);
                    if (string.IsNullOrWhiteSpace(regenerated))
                    {
                        _log.LogWarning("J.5a gate remediation produced empty reply — keeping original.");
                        return reply;
                    }

                    // J.5a re-evaluation (May 3, 2026) — regen output MUST pass the same
                    // gate stack. May 3 10:55 Failure C: J.5a remediation regen returned
                    // a byte-identical copy of the prior assistant turn from chat history;
                    // both sends went through Twilio ~57 seconds apart because the regen
                    // never re-ran the self-echo / coherence checks the original ran.
                    // SelfEchoInvariant (universal, May 3) plus this re-eval together
                    // close that class.
                    var regenArtifact = new CognitiveArtifact
                    {
                        Content                 = regenerated,
                        ProducerKind            = artifact.ProducerKind,
                        IntendedSink            = artifact.IntendedSink,
                        ContactName             = artifact.ContactName,
                        GeneratedAt             = DateTimeOffset.Now,
                        ContactRecentMessages   = artifact.ContactRecentMessages,
                        PriorAniMessages        = artifact.PriorAniMessages,
                        SystemPromptText        = artifact.SystemPromptText,
                        WriterInnerThought      = artifact.WriterInnerThought,
                    };

                    OutputGateResult regenResult;
                    try
                    {
                        regenResult = await _outputGate.EvaluateAsync(regenArtifact, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex,
                            "J.5a gate re-evaluation threw on regen — falling back to safe acknowledgement (regen would have dispatched uncovered).");
                        return SafeAcknowledgement;
                    }

                    if (regenResult.Verdict == OutputGateVerdict.Pass)
                    {
                        _log.LogInformation("J.5a gate remediation succeeded — regenerated reply passes gate.");
                        return regenerated;
                    }

                    _log.LogWarning(
                        "J.5a gate remediation FAILED re-eval [{Fired}] — verdict={Verdict}, hint={Hint}; falling back to safe acknowledgement.",
                        string.Join(",", regenResult.FiredInvariants), regenResult.Verdict, regenResult.RemediationHint);
                    return SafeAcknowledgement;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "J.5a gate remediation regeneration failed — keeping original reply.");
                    return reply;
                }

            case OutputGateVerdict.Fail:
                _log.LogWarning(
                    "J.5a gate Fail on reply [{Fired}]: {Hint} — dropping reply, using safe acknowledgement.",
                    string.Join(",", gateResult.FiredInvariants), gateResult.RemediationHint);
                return SafeAcknowledgement;

            default:
                return reply;
        }
    }

    private static string Truncate(string text, int maxLength)
        => ConversationFeatureDetector.Truncate(text, maxLength);

}
