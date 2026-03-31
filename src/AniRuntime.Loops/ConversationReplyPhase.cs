using AniRuntime.Actions;
using Mosaik.Core;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
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
    private readonly AniOptions _aniOptions;
    private readonly OllamaOptions _ollamaOptions;
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
        IOptions<AniOptions> aniOptions,
        IOptions<OllamaOptions> ollamaOptions,
        ILogger<ConversationReplyPhase> log)
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
        _aniOptions = aniOptions.Value;
        _ollamaOptions = ollamaOptions.Value;
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
        var snapshot = await _contextBuilder.BuildContextSnapshotAsync(perceptions, ct, emotionalState).ConfigureAwait(false);

        // Skip the entire retrieval pipeline in conversation mode.
        // The model gets: persona + conversation history. That's it.
        _log.LogDebug("Conversation mode: retrieval pipeline bypassed — conversation is the context");

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
            var confabCheck = DetectConversationConfabulation(reply, thread, lastMessage);
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
                        _log.LogDebug("No memories above confidence floor — keeping original reply");
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Confabulation grounding retrieval failed — keeping original reply");
                }
            }
        }

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

        // Echo guard: if the reply is nearly identical to something already in the thread
        // (Ani's prior messages OR Mark's messages), re-generate with a clean slate.
        // Self-echo = model parroting its own context window output.
        // Mark-echo = model parroting the contact's words back instead of engaging.
        // Always include Mark's last message in the check — parroting the contact's
        // words is the most common failure mode, especially in new threads.
        var priorMessages = thread.Messages
            .Where(m => m != replyMessage)
            .ToList();
        _log.LogDebug("Echo guard: checking reply against {Count} prior messages in thread", priorMessages.Count);
        if (priorMessages.Count > 0)
        {
            try
            {
                var replyEmbedding = await _ollama.EmbedAsync(reply, ct).ConfigureAwait(false);
                replyMessage.CachedEmbedding = replyEmbedding;
                foreach (var prior in priorMessages)
                {
                    prior.CachedEmbedding ??= await _ollama.EmbedAsync(prior.Content, ct).ConfigureAwait(false);
                    var similarity = VectorMath.CosineSimilarity(replyEmbedding, prior.CachedEmbedding);

                    // Self-echo: 0.80 catches paraphrased repetition (e.g. same core
                    // sentence with different opener/closer). Was 0.95 which only caught
                    // near-exact copies and missed "legs tucked under desk" repeats.
                    // Mark-echo: 0.85 catches parroting the contact's words back.
                    var threshold = prior.Role == Roles.Ani ? 0.80f : 0.85f;
                    if (similarity >= threshold)
                    {
                        var echoType = prior.Role == Roles.Ani ? "Self-echo" : "Mark-echo";
                        _log.LogWarning("{EchoType} detected (similarity={Similarity:F3}): reply matches prior {Role} message \"{Prior}\"",
                            echoType, similarity, prior.Role, prior.Content.Length > 60 ? prior.Content[..60] + "..." : prior.Content);

                        // Clean-slate re-generation: strip all retrieved context and conversation
                        // history to eliminate context contamination. The model failed because the
                        // context window was full of irrelevant fragments (coffee cups, snow, prior
                        // threads) that it tried to stitch into a response. Give it a clean environment
                        // with just persona grounding and the actual message.
                        // AC6: Include conversation thread summary so the clean-slate
                        // re-generation stays on topic. Without this, the model loses
                        // the thread and produces non-sequiturs ("cold noodles" when
                        // discussing Learned Geek Consulting).
                        var cs = snapshot.CharacterState;
                        // Use lastMessage (captured before reply was appended) — NOT
                        // thread.Messages[^1] which is now Ani's failed reply.
                        var lastMsg = lastMessage;
                        var threadSummary = thread.Messages.Count > 1
                            ? string.Join("\n", thread.Messages
                                .Where(m => m != replyMessage) // exclude the failed reply
                                .TakeLast(Math.Min(4, thread.Messages.Count))
                                .Select(m => $"{m.Role}: {Truncate(m.Content, 80)}"))
                            : "";
                        var threadContext = string.IsNullOrEmpty(threadSummary)
                            ? ""
                            : $"\n\nRecent conversation:\n{threadSummary}";
                        var contact = cs.PrimaryContactName ?? "Mark";
                        var cleanSystem = $"""
                            You are {cs.Name}, texting {contact}.
                            Your personality: {string.Join("; ", cs.CoreTraits)}.{threadContext}

                            {contact} just said something to you. Reply to THEIR message directly.
                            Stay on the topic THEY brought up. Do not change the subject.
                            Do not repeat what you already said. Find a new angle on the same topic.
                            Match the energy and length of the conversation.
                            Talk TO {contact}: "you", "your". Never third person.
                            Write ONLY the text message. No commentary, no quotation marks.
                            """;
                        var cleanUser = $"Do NOT repeat this (you already said it): \"{Truncate(reply, 80)}\"\n\n{contact} said: \"{lastMsg}\"";

                        // Moderate temperature — enough variation to avoid the same attractor,
                        // not so high that it goes off-topic
                        var retryReply = await _ollama.ChatAsync(
                            cleanSystem, Array.Empty<ChatMessage>(), cleanUser, ct, 0.7f)
                            .ConfigureAwait(false);
                        retryReply = CleanOutreachMessage(retryReply);

                        if (!string.IsNullOrWhiteSpace(retryReply))
                        {
                            _log.LogInformation("{EchoType} clean-slate re-generated: {Reply}", echoType, retryReply);
                            reply = retryReply;
                        }
                        else
                        {
                            _log.LogWarning("{EchoType} clean-slate re-generation produced empty reply — skipping", echoType);
                            return;
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Self-echo check failed — proceeding with original reply");
            }
        }

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
        if (emotionalState is not null && ConversationFeatureDetector.DetectCareGivingIntent(lastMessage))
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
    private static (bool IsConfabulated, string? Reason) DetectConversationConfabulation(
        string reply, ConversationThread thread, string lastMessage)
    {
        EnsureNlpInitialized();

        var replyLower = reply.ToLowerInvariant();

        // Build a set of topics/names/facts mentioned in the conversation
        var conversationText = string.Join(" ",
            thread.Messages.TakeLast(12).Select(m => m.Content.ToLowerInvariant()));

        // Check 1: Does the reply reference a specific person not mentioned in the conversation?
        // Uses Catalyst POS tagger to detect proper nouns (PROPN) — no hardcoded word lists.
        // The NLP model identifies "Kathy", "Hugh", "Laurie" as PROPN automatically.
        if (_nlpPipeline is not null)
        {
            try
            {
                var doc = new Catalyst.Document(reply, Mosaik.Core.Language.English);
                _nlpPipeline.ProcessSingle(doc);

                foreach (var span in doc)
                {
                    var properNouns = span.Tokens
                        .Where(t => t.POS == Catalyst.PartOfSpeech.PROPN)
                        .Select(t => t.Value)
                        .ToList();

                    foreach (var noun in properNouns)
                    {
                        if (noun.Length < 3) continue;
                        // Skip "I" which sometimes gets tagged as PROPN
                        if (noun is "I") continue;
                        if (!conversationText.Contains(noun.ToLowerInvariant()))
                            return (true, $"Reply mentions proper noun '{noun}' not in conversation");
                    }
                }
            }
            catch
            {
                // NLP failure is non-blocking — fall through to other checks
            }
        }

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
                // Check if the claimed reference is actually in the conversation
                var afterMarker = replyLower[(replyLower.IndexOf(marker) + marker.Length)..];
                var claimedTopic = afterMarker.Split('.', '!', '?', ',')[0].Trim();
                if (claimedTopic.Length > 3 && !conversationText.Contains(claimedTopic))
                    return (true, $"Reply claims shared history ('{marker}') about topic not in conversation");
            }
        }

        // Check 3: Does the reply assert specific facts (dates, times, numbers) not in the conversation?
        var replyNumbers = System.Text.RegularExpressions.Regex.Matches(reply, @"\b\d{2,}\b");
        foreach (System.Text.RegularExpressions.Match num in replyNumbers)
        {
            if (!conversationText.Contains(num.Value))
                return (true, $"Reply contains number '{num.Value}' not mentioned in conversation");
        }

        // Check 4: Does the reply make factual claims about self, contact, or relationship
        // that aren't established in the conversation? These are identity/activity confabulations
        // where the model invents plausible details about its own life, the contact's life,
        // or shared experiences. Triggers retrieval against profile/semantic memories.
        //
        // Self-claims: "I just finished a meeting", "I'm a developer", "my shift ends at..."
        // Contact-claims: "your class", "your sister", "your job at..."
        // Relationship-claims: "our anniversary", "that restaurant we went to"
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

    private static string Truncate(string text, int maxLength)
        => ConversationFeatureDetector.Truncate(text, maxLength);

}
