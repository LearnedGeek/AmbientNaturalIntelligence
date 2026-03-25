using AniRuntime.Actions;
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

        // Build context for the reply
        var snapshot = await _contextBuilder.BuildContextSnapshotAsync(perceptions, ct, emotionalState).ConfigureAwait(false);

        // Re-search relevant memories using semantic intent, not raw message text.
        // Intent extraction uses the 3B model to distill "Thanks yeah I decided to just
        // relax tonight. No work." → "relaxing at home, taking the evening off" — which
        // prevents retrieval contamination from shared surface tokens (e.g., "relaxing"
        // pulling in old "Sarah muscular therapy" memories).
        // AC1: Use scored search to apply confidence thresholding on cosine similarity.
        try
        {
            var searchIntent = await _intent.ExtractIntentAsync(lastMessage, ct).ConfigureAwait(false);
            var scoredResults = await _search.SearchWithScoresAsync(searchIntent, 5, ct).ConfigureAwait(false);
            var scoredList = scoredResults.ToList();

            // TF-IDF keyword extraction: search with distinctive words to find
            // topic-specific memories ("consulting business" → "Learned Geek")
            // that the full message embedding misses due to casual noise.
            var keywordQuery = await _keywords.ExtractSearchQueryAsync(lastMessage, 5, ct).ConfigureAwait(false);
            if (keywordQuery is not null)
            {
                var existingIds = new HashSet<Guid>(scoredList.Select(s => s.Record.Id));

                // Broad keyword search across all memory types
                var keywordResults = await _search.SearchWithScoresAsync(keywordQuery, 5, ct).ConfigureAwait(false);
                var newFromKeywords = keywordResults.Where(s => !existingIds.Contains(s.Record.Id)).ToList();
                if (newFromKeywords.Count > 0)
                {
                    _log.LogDebug("TF-IDF dual search: keyword query \"{Query}\" found {Count} additional memories",
                        keywordQuery, newFromKeywords.Count);
                    scoredList.AddRange(newFromKeywords);
                    foreach (var s in newFromKeywords) existingIds.Add(s.Record.Id);
                }

                // Targeted Semantic memory search — biographical facts, profile data,
                // and relationship knowledge live here. These are exactly the memories
                // that casual message search misses but keyword search should find.
                // Semantic memories represent user profile data that should always be
                // discoverable regardless of conversational phrasing.
                var semanticResults = await _search.SearchByTypeAsync(
                    keywordQuery, MemoryType.Semantic, 3, ct).ConfigureAwait(false);
                var newFromSemantic = semanticResults
                    .Where(r => !existingIds.Contains(r.Id))
                    .Select(r => new ScoredMemory(r, 1.0f, 1.0f)) // priority: always above confidence floor
                    .ToList();
                if (newFromSemantic.Count > 0)
                {
                    _log.LogDebug("Semantic priority search: \"{Query}\" found {Count} profile/fact memories",
                        keywordQuery, newFromSemantic.Count);
                    scoredList.AddRange(newFromSemantic);
                }
            }

            var confidenceFloor = (float)_aniOptions.RetrievalConfidenceFloor;

            // AC1: Filter out memories below the cosine similarity confidence floor.
            // Low-cosine matches are the primary driver of confabulation — the model
            // over-generalizes from semantically unrelated memories that happen to be
            // important or recent.
            var aboveFloor = scoredList.Where(s => s.CosineSimilarity >= confidenceFloor).ToList();
            var belowFloor = scoredList.Count - aboveFloor.Count;

            if (belowFloor > 0)
            {
                _log.LogDebug("AC1: Filtered {Filtered} memories below confidence floor {Floor:F2} (kept {Kept})",
                    belowFloor, confidenceFloor, aboveFloor.Count);
            }

            // AC1 + AC3: If ALL results fell below the floor, set the flag so prompt builders
            // inject an explicit null-result instruction instead of leaving context ambiguously empty.
            if (aboveFloor.Count == 0 && scoredList.Count > 0)
            {
                snapshot.RetrievalBelowConfidenceFloor = true;
                _log.LogInformation("AC1: All {Total} retrieved memories below confidence floor {Floor:F2} — null-result flag set",
                    scoredList.Count, confidenceFloor);
            }

            var messageRelevant = aboveFloor.Select(s => s.Record).ToList();

            // Filter out:
            // 1. The inbound message itself (just saved, would echo back as context)
            // 2. Closed conversation summaries
            // CS7: Filter out self-referential records — the inbound message echoed back as
            // both Episodic ("Mark said:") and Perception ("Mark texted:") records. Both are
            // echoes of the message being replied to and contaminate the context window.
            var contactName = snapshot.CharacterState.PrimaryContactName ?? "Mark";
            var msgPrefix30 = lastMessage.Length > 30 ? lastMessage[..30] : lastMessage;
            messageRelevant = messageRelevant
                .Where(m => !IsMessageEcho(m.Content, contactName, msgPrefix30))
                .Where(m => !m.Content.StartsWith(MemoryPrefixes.ConversationSummary))
                .ToList();

            // Re-rank for diversity against recent thoughts
            var recentThoughts = (await _search.GetByTypeAsync(MemoryType.InnerThought, 5, ct)
                .ConfigureAwait(false)).ToList();
            // CS6: Shared with BuildThoughtContextAsync — see ContextBuilder.ReRankForDiversityAsync
            messageRelevant = await _contextBuilder.ReRankForDiversityAsync(messageRelevant, recentThoughts, ct)
                .ConfigureAwait(false);

            // Keyword relevance boost: after diversity re-ranking, adjust scores so
            // topically relevant memories outrank generic ones ("Good morning") that
            // happen to score well on diversity metrics alone.
            if (keywordQuery is not null && messageRelevant.Count > 1)
            {
                var keywords = keywordQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var boosted = messageRelevant.Select(m =>
                {
                    var contentLower = m.Content.ToLowerInvariant();
                    bool hasKeyword = keywords.Any(k => contentLower.Contains(k.ToLowerInvariant()));
                    float boost = hasKeyword ? 0.15f : -0.10f;
                    return (record: m, boost);
                })
                .OrderByDescending(x => x.boost)
                .ThenBy(x => messageRelevant.IndexOf(x.record)) // preserve diversity order within same boost tier
                .Select(x => x.record)
                .ToList();

                if (!boosted.SequenceEqual(messageRelevant))
                {
                    _log.LogDebug("Keyword relevance boost: reordered {Count} memories using keywords \"{Keywords}\"",
                        messageRelevant.Count, keywordQuery);
                }
                messageRelevant = boosted;
            }

            // AC6: Topic-mismatch detection DISABLED.
            // Over-triggered on casual conversation — flagged nearly every message,
            // causing all memories to be skipped. The confidence floor (0.60) already
            // filters irrelevant memories. If a memory passes the floor, let the model see it.
            // The raw model handles irrelevant context naturally.

            snapshot.RelevantMemory = messageRelevant;
            _log.LogDebug("Conversation context: re-searched with message text, {Count} relevant memories (confidence floor={Floor:F2})",
                messageRelevant.Count, confidenceFloor);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Conversation memory re-search failed — using perception-based results");
        }

        // Populate RecentHistory with a recency-windowed view of the conversation thread.
        // Feature 34 (MemGPT): Context compression for long conversations.
        // When threads exceed the window, older messages are summarized into a brief
        // recap rather than silently dropped. Preserves early context in compressed form.
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

        // Feature 10: Receiving Care — detect when contact is checking in on Ani
        if (emotionalState is not null && ConversationFeatureDetector.DetectCareGivingIntent(lastMessage))
        {
            _log.LogInformation("Care detected in message — creating care contribution");
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
                _log.LogInformation("Lexical anchors triggered: {Count} — contributions created", anchorContributions.Count);
            }
        }

        // Feature 18: Reactive Withdrawal — detect dismissive or hurtful intent.
        if (emotionalState is not null && ConversationFeatureDetector.DetectHurtIntent(lastMessage))
        {
            _log.LogInformation("Hurt detected in message — creating H1 withdrawal contribution");
            await _emotional.SaveDirectContributionAsync(emotionalState,
                "hurt detected — pulling back emotionally",
                warmth: -0.12f, energy: -0.10f, worry: -0.15f, playfulness: -0.10f,
                ImpactCategory.Conversation, ct).ConfigureAwait(false);

            _withdrawalExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_aniOptions.WithdrawalDurationMinutes);
            _log.LogInformation("Withdrawal active until {Expires}", _withdrawalExpiresAt.Value.ToString("HH:mm"));

            // Save as inner thought so future cycles can reference "something felt off"
            await _persist.SaveAsync(new MemoryRecord
            {
                Type       = MemoryType.InnerThought,
                Content    = "Something in that last message landed in a way that stung a little. I'm still here, just... quieter.",
                Importance = 0.6f,
            }, ct).ConfigureAwait(false);
        }

        // Feature 18: Pass withdrawal state to prompt builder for tone injection
        snapshot.IsWithdrawn = IsWithdrawn;

        // Feature 15 Layer 3: Contradiction detection disabled — the LLM call produced
        // false positives on casual conversation ("are you sick?" vs "what's going on?"
        // flagged as contradictory). Contradiction warnings no longer injected into prompt
        // either (removed in Phase C), so this was just burning cycles for noisy logs.

        // Feature 14: Claim extraction removed — v6 trained on honest uncertainty.
        // The LLM call to extract and verify claims added latency without improving
        // conversation quality. The model handles unknown topics naturally.

        // Step 2: Generate reply (free text, using conversation model)
        // AC4: Temperature splitting — use lower temperature when memories are grounded
        // (reduces confabulation on factual claims) vs standard for creative expression.
        var hasGroundedMemories = snapshot.RelevantMemory.Count > 0 && !snapshot.RetrievalBelowConfidenceFloor;
        var replyTemperature = hasGroundedMemories
            ? _ollamaOptions.MemoryGroundedTemperature
            : _ollamaOptions.CreativeTemperature;
        _log.LogDebug("AC4: Temperature={Temperature:F1} (grounded={Grounded})", replyTemperature, hasGroundedMemories);

        var replyPrompt = isReconsideration
            ? PromptBuilder.BuildReconsiderationReplyPrompt(snapshot, thread)
            : PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);
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

        // AC2/UP1 removed — v6 was trained on honest uncertainty and anti-confabulation.
        // Both models handle unknown topics naturally when the pipeline doesn't drown them
        // in irrelevant context. The re-generation calls added 2 extra LLM round-trips
        // and produced worse output (clean-slate prompts lost conversation history).

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
        var priorMessages = thread.Messages
            .Where(m => m != replyMessage && m != thread.Messages.Last(x => x.Role == Roles.Mark))
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
                        var cleanSystem = $"""
                            You are {cs.Name}. You are in a warm, established relationship with {cs.PrimaryContactName ?? "Mark"}.
                            Your personality: {string.Join("; ", cs.CoreTraits)}.{threadContext}

                            RULES:
                            - Respond naturally to what they just said. This is a real conversation.
                            - 1-3 sentences max. Thumb-typed phone text.
                            - Say something DIFFERENT from what you just said — you already tried that.
                            - Be honest — "I don't think you've mentioned that" or "wait, tell me more" is warm and real.
                            - Do NOT invent details, do NOT narrate what they did, do NOT use third person.
                            - Write ONLY the text message. No commentary, no quotation marks.
                            """;
                        var cleanUser = $"You already said: \"{reply}\" — say something DIFFERENT.\nThey just said: \"{lastMsg}\"";

                        // Higher temperature on echo retry to force variation
                        var retryReply = await _ollama.ChatAsync(
                            cleanSystem, Array.Empty<ChatMessage>(), cleanUser, ct, 0.9f)
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

        // Step 5: Persist Ani's reply to DB (already added to in-memory thread before echo guard)
        // Update content in case echo guard replaced it.
        replyMessage.Content = reply;
        await _conversations.AddMessageAsync(thread.Id, replyMessage, ct).ConfigureAwait(false);

        // Update desire — contact happened
        await _desire.ResetAfterOutreachAsync(ct).ConfigureAwait(false);

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
