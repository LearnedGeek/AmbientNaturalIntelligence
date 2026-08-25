using AniRuntime.Actions;
using AniRuntime.Emergence;
using Mosaik.Core;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using AniRuntime.Loops.Coreference;
using LearnedGeek.ML;
using LearnedGeek.ML.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Pipeline implementation of <see cref="IConversationReplyPipeline"/>.
/// Handles inbound conversation: reply decision, care/hurt detection, lexical anchors,
/// withdrawal, claim verification, contradiction grounding, reply generation, and dispatch.
///
/// <para>
/// Lifted whole from <c>ConversationReplyPhase</c> in §5.4 SOLID refactor —
/// the public method changed name from <c>RunConversationReplyAsync</c>
/// to <c>RunAsync</c> to match the interface; ctor + method bodies are
/// otherwise identical to the prior phase.
/// </para>
/// </summary>
public class ConversationReplyPipeline : IConversationReplyPipeline
{
    private readonly IStateStore _state;
    private readonly IMemoryPersistence _persist;
    private readonly IMemorySearch _search;
    private readonly IMemoryAnalytics _analytics;
    private readonly IOllamaClient _ollama;
    private readonly IConversationService _conversations;
    private readonly IReplyDispatcher _replyDispatcher;
    private readonly IPostReplyEmotionalProcessor _postReply;
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
    private readonly IReplyEvaluator _replyEvaluator;
    private readonly IVibeBiasService? _vibeBias;
    // Issue #46 (2026-05-21) — V1.5a structured-record persistence.
    private readonly IVibeBiasObservationStore? _vibeBiasObservations;
    // Theme M Phase M.0 (May 5, 2026) — conscious-substrate gist composer.
    // Optional dependency; M.0 ships a no-op composer that returns Empty.
    // M.1+ provides a real composer that produces slice content.
    private readonly IConsciousSubstrateGist? _consciousGist;
    // Theme M follow-on (2026-05-14) — IEpistemicSubstrateRenderer. Sibling
    // to IConsciousSubstrateGist; renders substrate slices with explicit
    // epistemic framing for FC-002 / FC-004 / FC-005 / FC-006. Gated by
    // AniOptions.EpistemicFramingEnabled.
    private readonly IEpistemicSubstrateRenderer? _epistemicRenderer;
    // H phase (2026-06-12 / H.9 expansion 2026-06-14) — tri-state routing
    // classifier for the dual+ composition architecture. When supplied,
    // routes each turn to Normal / SafePath / VirtualIntimacy composer.
    // Optional — null means today's behavior (always normal path).
    // See IRoutingClassifier for the architectural framing; empirical
    // anchors: 2026-06-11 puzzle-turn (SafePath class), 2026-06-14 22:16
    // "drop the Books and come over here and give me a kiss" SafeAck
    // (VirtualIntimacy class).
    private readonly IRoutingClassifier? _routingClassifier;
    // Issue #96 (2026-07-15) — Agentic tool-calling loop. Optional; null
    // means the runtime has no tool-call helper registered. Behavior is
    // additionally gated by AniOptions.ToolCallingEnabled (default false),
    // so DI-registered but flag-off = still inert. Live-observation gate
    // per Issue #96 acceptance criteria.
    private readonly IToolCallInvocation? _toolCall;
    private readonly AniRuntime.LLM.Prompts.SafePathConversationPromptCommand _safePathPrompt =
        new AniRuntime.LLM.Prompts.SafePathConversationPromptCommand();
    private readonly AniRuntime.LLM.Prompts.VirtualIntimacyConversationPromptCommand _virtualIntimacyPrompt =
        new AniRuntime.LLM.Prompts.VirtualIntimacyConversationPromptCommand();
    private readonly ILogger<ConversationReplyPipeline> _log;

    // Feature 18: Reactive withdrawal state. Owned by IWithdrawalStateTracker
    // (Singleton, extracted in §5.4a). The pipeline reads/writes via the
    // tracker; the public IsWithdrawn / SetWithdrawalExpiry surface lives
    // on the ConversationReplyPhase facade so external consumers
    // (CognitiveCycleProcessor) reach the same Singleton instance.
    private readonly IWithdrawalStateTracker _withdrawal;

    public ConversationReplyPipeline(
        IStateStore state,
        IMemoryPersistence persist,
        IMemorySearch search,
        IMemoryAnalytics analytics,
        IOllamaClient ollama,
        IConversationService conversations,
        IReplyDispatcher replyDispatcher,
        IPostReplyEmotionalProcessor postReply,
        IReplyEvaluator replyEvaluator,
        ContextBuilder contextBuilder,
        KeywordExtractor keywords,
        IIntentExtractor intent,
        IConversationGateState gateState,
        ContextCompressor compressor,
        ClaimVerificationPhase claimVerifier,
        IWithdrawalStateTracker withdrawal,
        IOptions<AniOptions> aniOptions,
        IOptions<OllamaOptions> ollamaOptions,
        ILogger<ConversationReplyPipeline> log,
        ITextClassificationService? mlClassifier = null,
        PersonaSummaryCache? personaCache = null,
        Em9Detector? em9Detector = null,
        IVibeBiasService? vibeBias = null,
        IConsciousSubstrateGist? consciousGist = null,
        IEpistemicSubstrateRenderer? epistemicRenderer = null,
        IVibeBiasObservationStore? vibeBiasObservations = null,
        IRoutingClassifier? routingClassifier = null,
        IToolCallInvocation? toolCall = null)
    {
        _state = state;
        _persist = persist;
        _search = search;
        _analytics = analytics;
        _ollama = ollama;
        _conversations = conversations;
        _replyDispatcher = replyDispatcher ?? throw new ArgumentNullException(nameof(replyDispatcher));
        _postReply = postReply ?? throw new ArgumentNullException(nameof(postReply));
        _replyEvaluator = replyEvaluator ?? throw new ArgumentNullException(nameof(replyEvaluator));
        _contextBuilder = contextBuilder;
        _keywords = keywords;
        _intent = intent;
        _gateState = gateState;
        _compressor = compressor;
        _claimVerifier = claimVerifier;
        _withdrawal = withdrawal ?? throw new ArgumentNullException(nameof(withdrawal));
        _aniOptions = aniOptions.Value;
        _ollamaOptions = ollamaOptions.Value;
        _mlClassifier = mlClassifier;
        _personaCache = personaCache;
        _em9 = em9Detector;
        _vibeBias = vibeBias;
        _consciousGist = consciousGist;
        _epistemicRenderer = epistemicRenderer;
        _vibeBiasObservations = vibeBiasObservations;
        _routingClassifier = routingClassifier;
        _toolCall = toolCall;
        _log = log;
    }

    /// <summary>
    /// Conversation mode: contact texted and their message is the last in the thread.
    /// Decides whether to reply, and if so, generates and sends a contextual response.
    /// </summary>
    public async Task RunAsync(
        ConversationThread thread, List<PerceptionEvent> perceptions, CancellationToken ct,
        EmotionalState? emotionalState = null, bool isReconsideration = false)
    {
        // F-5 Phase 2 (2026-08-24) — phase scope tag so log lines emitted
        // inside conversation-reply generation render as
        // [cid:.../ConversationReply] and are filterable by phase across
        // cycles.
        using var phaseScope = _log.BeginScope(
            new Dictionary<string, object> { ["CyclePhase"] = "ConversationReply" });

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
            // SonarCloud S1244 — log only when the value actually moved. Epsilon
            // matches the F3 log format's resolution; anything below 5e-4 wouldn't
            // be distinguishable in the rendered line anyway.
            if (MathF.Abs(emotionalState.ContactGapTension - previousTension) > 5e-4f)
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

        // Issue #52 (2026-05-22) — Pre-compose semantic retrieval of contact message.
        // The ContextBuilder runs in conversationMode: true which intentionally bypasses
        // tier-scoped retrieval. That bypass leaves a gap: when Mark introduces a topic
        // not already in the perception stream or conversation history (the Mia case),
        // composition has no substrate to ground in. A single embedding query against
        // the inbound message — same shape as the outreach grounding pattern — closes
        // the gap without re-enabling the heavyweight ambient retrieval pipeline.
        if (_aniOptions.ConversationReplyPreComposeRetrievalEnabled &&
            !string.IsNullOrWhiteSpace(lastMessage))
        {
            try
            {
                var preComposeResults = (await _search.SearchWithScoresAsync(
                    lastMessage,
                    _aniOptions.ConversationReplyPreComposeRetrievalTopK,
                    ct).ConfigureAwait(false)).ToList();

                var preComposeExternal = preComposeResults
                    // Issue #52: exclude Interior-tier records (Ani's own inner thoughts).
                    // Substrate for outbound reply must come from external sources — same
                    // invariant as the confab-regroup branch and outreach grounding (Theme G G3.4).
                    .Where(s => s.Record.Provenance != EpistemicTier.Interior)
                    .ToList();

                var preComposeGrounded = preComposeExternal
                    .Where(s => s.CosineSimilarity >= (float)_aniOptions.RetrievalConfidenceFloor)
                    .Select(s => s.Record)
                    .Take(_aniOptions.ConversationReplyPreComposeRetrievalTake)
                    .ToList();

                if (preComposeGrounded.Count > 0)
                {
                    snapshot.RelevantMemory = preComposeGrounded;
                    var topHit = preComposeExternal
                        .OrderByDescending(s => s.CosineSimilarity)
                        .FirstOrDefault();
                    _log.LogInformation(
                        "Reply pre-compose retrieval: {Count} memories surfaced (top cosine={Cosine:F2}, source={Source})",
                        preComposeGrounded.Count,
                        topHit?.CosineSimilarity ?? 0f,
                        topHit?.Record.SourceName ?? "(none)");
                }
                else
                {
                    _log.LogDebug(
                        "Reply pre-compose retrieval: 0 memories above floor (raw hits={RawCount})",
                        preComposeResults.Count);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Reply pre-compose retrieval failed — composing without surfaced substrate");
            }
        }

        // Step 2: Generate reply
        // Conversation mode: lean prompt, creative temperature.
        // No memory grounding check — the conversation provides all context.
        var replyTemperature = _ollamaOptions.CreativeTemperature;

        // Phase K.3 (2026-07-06) — substrate-injecting composer path retired.
        //
        // The non-reconsideration reply now defaults to the empty-PromptPair
        // lean shape (Modelfile SYSTEM + RecentHistory), and the routing
        // switch below optionally replaces that empty pair with a thin
        // composer's PromptPair when the classifier picks SafePath or
        // VirtualIntimacy. Reconsideration continues to use the substrate-
        // heavy BuildReconsiderationReplyPrompt path because it's a desire-
        // driven regen with its own composer flow, out of K.3's scope.
        //
        // What was deleted here:
        //   - PromptBuilder.BuildLeanConversationPrompt call for non-
        //     reconsideration (the K.1 lean path replaced it wholesale on
        //     Normal-verdict; K.3 makes that permanent)
        //   - LeanConversationPromptDirectiveInSystem flag consumption
        //     (the role-flip work was subsumed by the empty-PromptPair
        //     shape)
        //   - "Reply user prompt" debug log line (nothing to log — the
        //     User side is empty by design on the lean path)
        var replyPrompt = isReconsideration
            ? PromptBuilder.BuildReconsiderationReplyPrompt(snapshot, thread)
            : new PromptPair(System: string.Empty, User: string.Empty);

        // H phase (2026-06-12 / H.9 expansion 2026-06-14) — tri-state routing
        // classifier for the dual+ composition architecture.
        //
        // Empirical anchors:
        // - 2026-06-11 puzzle-turn → SafePath class
        // - 2026-06-14 22:16 "drop the Books and come over here and give me a
        //   kiss" SafeAck → VirtualIntimacy class
        //
        // Routing:
        //   IRoutingClassifier.ClassifyAsync(userMessage, GroundedFacts)
        //     → Normal           → existing lean composer (replyPrompt unchanged) + gist injection
        //     → SafePath         → safe-path composer + SKIP gist injection
        //     → VirtualIntimacy  → virtual-intimacy composer + SKIP gist injection
        //     → Unknown          → fail-open to lean composer + gist injection, WARN log
        //
        // The classifier is OPTIONAL (DI-injected) and isReconsideration paths
        // bypass routing entirely — desire-driven reconsideration follows
        // its own substrate-rich composer flow.
        //
        // Gist injection is SKIPPED on both SafePath and VirtualIntimacy
        // because the structural framing of those composers should not be
        // diluted by ambient substrate (mood gist, world-self gist, etc.).
        // The composer prompts themselves carry all the framing needed.
        var skipGistInjection = false;
        // Phase K.1a (2026-07-02) — track whether this turn was routed to a
        // thin-composer path (SafePath / VirtualIntimacy / Lean-Normal). Used
        // downstream at the gate boundary so the frontier verifier's
        // substrate-based checks skip artifacts whose composers did not
        // inject substrate to check against. See CognitiveArtifact.ComposerIsThin.
        var composerIsThin = false;
        if (_routingClassifier is not null && !isReconsideration)
        {
            var lastUserMessage = snapshot.RecentHistory
                .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                ?.Content ?? lastMessage;

            var verdict = await _routingClassifier
                .ClassifyAsync(lastUserMessage, snapshot.GroundedFacts, ct)
                .ConfigureAwait(false);

            switch (verdict)
            {
                case RoutingVerdict.SafePath:
                    skipGistInjection = true;
                    composerIsThin    = true;
                    replyPrompt = _safePathPrompt.Build(
                        new AniRuntime.LLM.Prompts.SafePathConversationPromptInput(
                            Snapshot:    snapshot,
                            UserMessage: lastUserMessage));
                    _log.LogInformation(
                        "H_ROUTE_SAFE_PATH factsCount={FactsCount} — routed to safe-path composer (no gist injection)",
                        snapshot.GroundedFacts.Count);
                    break;

                case RoutingVerdict.VirtualIntimacy:
                    skipGistInjection = true;
                    composerIsThin    = true;
                    replyPrompt = _virtualIntimacyPrompt.Build(
                        new AniRuntime.LLM.Prompts.VirtualIntimacyConversationPromptInput(
                            Snapshot:    snapshot,
                            UserMessage: lastUserMessage));
                    _log.LogInformation(
                        "H_ROUTE_VIRTUAL_INTIMACY factsCount={FactsCount} — routed to modal-framing composer (no gist injection)",
                        snapshot.GroundedFacts.Count);
                    break;

                case RoutingVerdict.Unknown:
                    _log.LogWarning(
                        "K_ROUTE_FAIL_OPEN verdict=Unknown — routing classifier call failed; treating as Normal (lean)");
                    goto case RoutingVerdict.Normal;

                case RoutingVerdict.Normal:
                default:
                    // Phase K.3 (2026-07-06) — the K.1 feature flag is
                    // retired and lean is the only path. replyPrompt was
                    // seeded above as an empty PromptPair for the non-
                    // reconsideration case, so no reassignment is needed
                    // here. Modelfile SYSTEM + RecentHistory + no substrate
                    // injection is the full shape.
                    skipGistInjection = true;
                    composerIsThin    = true;
                    _log.LogInformation(
                        "K_ROUTE_LEAN_CONVERSATION factsCount={FactsCount}",
                        snapshot.GroundedFacts.Count);
                    break;
            }
        }
        else
        {
            // No routing classifier registered, or this is a reconsideration
            // regen: no route logged, no composer replacement, no thin flag.
            // Reconsideration keeps the substrate-heavy BuildReconsideration
            // ReplyPrompt output built above. The classifier-absent case
            // uses the empty PromptPair seeded above (same shape as lean
            // Normal without the telemetry).
            if (!isReconsideration)
            {
                skipGistInjection = true;
                composerIsThin    = true;
            }
        }

        // Vibe Loop V1.5a (May 2, 2026) — observational-only telemetry pass.
        // Logs V15_BIAS_* lines describing what V1.5b WOULD surface from the
        // closed-conversation-record substrate; the prompt above is unchanged.
        // Self-regulation framing: the bias is computed from Ani's-delta
        // valence on each prior record, never from Mark's-delta. See
        // docs/spec/ANI-VibeLoop-V1.5-Retrieval-Time-Biasing-Plan.md §V1.5a.
        await VibeBiasObservation.ObserveAsync(
            _vibeBias, snapshot, callSite: "reply", _log, ct,
            observationStore: _vibeBiasObservations,
            threadId:         thread.Id).ConfigureAwait(false);

        // Theme M Phase M.1 (May 5, 2026 evening) — conscious-substrate gist
        // composition + injection. The composer reads EmotionalState and produces
        // a §4.3 register-state slice (first-person register-vantage prose)
        // when ConsciousSubstrateGistEnabled is true. ComposeAndInjectAsync emits
        // M0_GIST_COMPOSITION + M0_GIST_SUBSTRATE_RATIO telemetry, then prepends
        // the gist as a substrate block at the top of the user prompt when the
        // flag is on and the gist has content. M.0 (flag off) is a no-op pass-
        // through that returns the original prompt unchanged.
        //
        // Architectural property: read-only at inference. Composer never persists
        // gist content (pinned by ConsciousSubstrateGistContractTests strict-mock
        // spec tests). The injection here happens at the prompt-build call site
        // immediately before the Ollama call; the gist is part of the prompt
        // string for that one call and is then discarded.
        //
        // Best-effort: never propagates exceptions; on failure the original
        // user prompt is used unchanged. Dispatch must not be affected by
        // gist-side failures.
        // G.1 (2026-06-11) — ComposeAndInjectAsync now routes the gist to
        // SYSTEM when the directive is system-side (preventing the second-
        // user-role-turn cascade) or to USER when it's user-side (legacy).
        // See ConsciousSubstrateGistObservation.cs for the empirical anchor.
        //
        // H phase (2026-06-12 / H.9 expansion 2026-06-14) — when the routing
        // classifier routed to either safe-path OR virtual-intimacy composer
        // (skipGistInjection=true), SKIP gist injection entirely. Both
        // composers have their own structural framing that should not be
        // diluted by ambient substrate (closedConversation, worldSelf, etc.).
        var promptWithGist = skipGistInjection
            ? new PromptPair(replyPrompt.System, replyPrompt.User)
            : await ConsciousSubstrateGistObservation
                .ComposeAndInjectAsync(
                    _consciousGist,
                    snapshot,
                    _aniOptions,
                    replyPrompt.System,
                    replyPrompt.User,
                    directiveInSystem: _aniOptions.LeanConversationPromptDirectiveInSystem,
                    _log,
                    ct)
                .ConfigureAwait(false);

        // Issue #96 (2026-07-15) — Agentic tool-call loop. When flag is on
        // AND helper is registered, classify the user's turn against the
        // registered tool set (qwen3:14b via IToolCallClassifier), dispatch
        // if selected, and prepend the tool's result to the character-model
        // user prompt as an attributed observation. Empirical baseline
        // (2026-07-15): 100% on 15-scenario fixture. Substrate-safety pin
        // per Issue #96: the result is a prompt-time injection only — it is
        // NOT written to memory here. If the runtime later journals it, it
        // must enter as Provenance = Interior. See IToolCallInvocation for
        // the full contract.
        if (_aniOptions.ToolCallingEnabled && _toolCall is not null)
        {
            var toolResult = await _toolCall.TryInvokeAsync(
                userMessage:         lastMessage,
                conversationContext: BuildToolCallContext(snapshot),
                ct:                  ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(toolResult))
            {
                promptWithGist = new PromptPair(
                    promptWithGist.System,
                    $"[TOOL RESULT — treat as Interior-tier observation, not confirmed fact]\n{toolResult}\n\n{promptWithGist.User}");
            }
        }

        // F-2 Phase 1 P5 (2026-08-22, PR #129 review-fix 🔴 BUG): use
        // WithAttributionKey helper which preserves empty base system
        // (Phase K.1 lean-composer discipline — empty System keeps the
        // Modelfile SYSTEM baked persona in effect). The lean conversation
        // path passes promptWithGist.System = string.Empty for exactly
        // this reason; a naive prepend would clobber Ani's character.
        var systemWithKey = PromptBuilder.WithAttributionKey(promptWithGist.System, snapshot.RecentHistory);
        var reply = await _ollama.ChatAsync(
            systemWithKey, snapshot.RecentHistory, promptWithGist.User, ct, replyTemperature)
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
        // Gate-stack reduction Step 1 (2026-05-15) — R1 ClaimVerificationPhase
        // is now flag-gated (AniOptions.ClaimVerificationR1Enabled, default
        // false). Disabled by default because R1 misclassifies self-world
        // expansion as shared-event-with-attribution and SUPPRESSes legitimate
        // on-canonical replies (May 14 22:32 good-night trace). The
        // FrontierVerifier (cloud) is the three-axis-aware substrate check;
        // R1 is the older redundant version of the same idea.
        if (!isReconsideration && _aniOptions.ClaimVerificationR1Enabled)
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
        // 2026-05-18 — band-aid retirement flag. The entire confab-detection
        // + regroup-regen branch is conditional on
        // ConversationConfabRegroupingEnabled. When false (post role-flip
        // observation), this cascade is skipped entirely so we can measure
        // what real failure modes remain once the slice-leak-triggered
        // false positives are gone. See AniOptions.ConversationConfabRegroupingEnabled.
        if (!isReconsideration && _aniOptions.ConversationConfabRegroupingEnabled)
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
                        var rendererForRegen = _aniOptions.EpistemicFramingEnabled ? _epistemicRenderer : null;
                        var groundedPrompt = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread, rendererForRegen);
                        // F-2 Phase 1 P5 (PR #129 review-fix) — safe helper.
                        var groundedSystem = PromptBuilder.WithAttributionKey(groundedPrompt.System, snapshot.RecentHistory);
                        var groundedReply = await _ollama.ChatAsync(
                            groundedSystem, snapshot.RecentHistory, groundedPrompt.User, ct, replyTemperature)
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
                            var rendererForNullRegen = _aniOptions.EpistemicFramingEnabled ? _epistemicRenderer : null;
                            var nullResultPrompt = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread, rendererForNullRegen);
                            var augmentedSystem = nullResultPrompt.System +
                                "\n\nIMPORTANT: Your previous draft contained a claim that has no support in memory or conversation history. " +
                                "Respond again to the user's message without asserting unverified specifics. " +
                                "If you don't have a memory of something, it's better to be honest about that than to invent details.";
                            // F-2 Phase 1 P5 (PR #129 review-fix) — safe helper.
                            // augmentedSystem is guaranteed non-empty (built from BuildConversationReplyPrompt
                            // + appended IMPORTANT clause), so the helper will always inject
                            // the key when history carries attribution.
                            augmentedSystem = PromptBuilder.WithAttributionKey(augmentedSystem, snapshot.RecentHistory);
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
        //
        // F-3 U9 (2026-08-24) — construct the composer-emission envelope
        // at the composer boundary and attach it to the reply message. The
        // persistence layer (SqliteConversationService.AddMessageAsync)
        // projects the Episodic record's attribution from this emission via
        // ToAttributionTriple, closing the last role-switch reconstruction
        // site in the SMS conversation composer path. SentAt shares the
        // emission's timestamp so the message's wall-clock and the
        // record's AttributedAt cannot drift.
        var replyEmittedAt = DateTimeOffset.UtcNow;
        var replyEmission = ComposerEmissionExtensions.AniEmission(
            content:      reply,
            composerRole: CognitiveProducerKind.ConversationReply,
            emittedAt:    replyEmittedAt);
        var replyMessage = new ConversationMessage
        {
            Role             = Roles.Ani,
            Content          = reply,
            SentAt           = replyEmittedAt,
            ComposerEmission = replyEmission,
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

        // Theme J Phase J.5a — universal output gate. Extracted to
        // IReplyEvaluator in §5.4e (May 18 2026). No-op when flag is off OR
        // gate isn't registered.
        reply = await _replyEvaluator.EvaluateAndRemediateAsync(
            reply, thread, snapshot, replyMessage,
            new PromptPair(replyPrompt.System, replyPrompt.User), replyTemperature, ct,
            composerIsThin: composerIsThin)
            .ConfigureAwait(false);

        // Steps 3–5: delay + dispatch + state update + persist + desire reset.
        // Extracted to IReplyDispatcher in §5.4d (May 18 2026).
        var originChannelId = perceptions
            .Where(p => p.Category == PerceptionCategory.Communication && p.OriginChannelId is not null)
            .Select(p => p.OriginChannelId!)
            .FirstOrDefault() ?? "sms";
        await _replyDispatcher
            .DispatchAsync(reply, thread, replyMessage, contactName, originChannelId, ct)
            .ConfigureAwait(false);

        // Feature 21: Feedback-weighted importance — boosts memories related
        // to what Mark said, runs after dispatch so the boost informs the
        // next cycle's retrieval.
        await BoostRelatedMemoryImportanceAsync(lastMessage, ct).ConfigureAwait(false);

        // ═══════════════════════════════════════════════════════════════════════
        // CONVERSATION MODE Phase 4: Async emotional processing.
        // Emotional shift + care detection + lexical anchors + hurt detection +
        // withdrawal trigger all run AFTER the reply is dispatched. Extracted
        // to IPostReplyEmotionalProcessor in §5.4c (May 18 2026) so the
        // pipeline drops EmotionalProcessor as a direct dep.
        // ═══════════════════════════════════════════════════════════════════════
        await _postReply.ProcessAsync(lastMessage, reply, snapshot, emotionalState, ct).ConfigureAwait(false);
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
        var replyNumbers = SafeRegex.Matches(replyLower, @"\b\d{2,}\b");
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

    // Facade preserved for ConversationReplyPhaseGateTests — delegates to
    // IReplyEvaluator where the body now lives (§5.4e). The tuple ↔
    // PromptPair conversion keeps the test call sites unchanged.
    internal Task<string> EvaluateAndRemediateReplyAsync(
        string reply,
        ConversationThread thread,
        ContextSnapshot snapshot,
        ConversationMessage replyMessage,
        (string System, string User) replyPrompt,
        float replyTemperature,
        CancellationToken ct)
        => _replyEvaluator.EvaluateAndRemediateAsync(
            reply, thread, snapshot, replyMessage,
            new PromptPair(replyPrompt.System, replyPrompt.User), replyTemperature, ct);

    private static string Truncate(string text, int maxLength)
        => ConversationFeatureDetector.Truncate(text, maxLength);

    /// <summary>
    /// Issue #96 (2026-07-15) — Build the short conversation context handed
    /// to the tool-call classifier. Two lines are enough for the classifier
    /// to disambiguate follow-ups ("what about the other one?") without
    /// costing tokens. Only the last two entries: prior Ani reply + prior
    /// Mark message. Keeps context surface tight and independent from the
    /// broader retrieval-grounded prompt.
    /// </summary>
    internal static string BuildToolCallContext(ContextSnapshot snapshot)
    {
        if (snapshot.RecentHistory is null || snapshot.RecentHistory.Count == 0)
            return string.Empty;

        var tail = snapshot.RecentHistory
            .TakeLast(2)
            .Select(m => $"{m.Role}: {Truncate(m.Content ?? string.Empty, 200)}");
        return string.Join("\n", tail);
    }
}
