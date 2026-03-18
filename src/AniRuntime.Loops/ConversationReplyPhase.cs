using System.Text.Json;
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
    private readonly IMemoryService _memory;
    private readonly IOllamaClient _ollama;
    private readonly IConversationService _conversations;
    private readonly AniActionDispatcher _dispatcher;
    private readonly DesireEngine _desire;
    private readonly EmotionalProcessor _emotional;
    private readonly ContextBuilder _contextBuilder;
    private readonly KeywordExtractor _keywords;
    private readonly AniOptions _aniOptions;
    private readonly OllamaOptions _ollamaOptions;
    private readonly ILogger<ConversationReplyPhase> _log;

    // Feature 18: Reactive withdrawal — transient emotional state after hurt detection.
    // Suppresses outreach and injects quieter tone during the withdrawal window.
    private DateTimeOffset? _withdrawalExpiresAt;
    internal bool IsWithdrawn => _withdrawalExpiresAt.HasValue && DateTimeOffset.UtcNow < _withdrawalExpiresAt.Value;

    // Tracks the last contact message we evaluated a reply decision for.
    // Once Ani decides "NO" on a specific message, she won't re-evaluate it every cycle.
    // Resets when a new message arrives (different SentAt timestamp).
    // Public so the heartbeat can check whether the contact's last message has already
    // been evaluated — if so, use ambient timing instead of conversation heartbeat.
    public DateTimeOffset? LastEvaluatedMessageAt { get; internal set; }

    public ConversationReplyPhase(
        IMemoryService memory,
        IOllamaClient ollama,
        IConversationService conversations,
        AniActionDispatcher dispatcher,
        DesireEngine desire,
        EmotionalProcessor emotional,
        ContextBuilder contextBuilder,
        KeywordExtractor keywords,
        IOptions<AniOptions> aniOptions,
        IOptions<OllamaOptions> ollamaOptions,
        ILogger<ConversationReplyPhase> log)
    {
        _memory = memory;
        _ollama = ollama;
        _conversations = conversations;
        _dispatcher = dispatcher;
        _desire = desire;
        _emotional = emotional;
        _contextBuilder = contextBuilder;
        _keywords = keywords;
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

        // Check 1: is this a terminal message that doesn't need a reply?
        if (IsTerminalMessage(lastMessage))
        {
            _log.LogInformation("Terminal message detected (\"{Message}\") — no reply needed", lastMessage);
            return;
        }

        // Build context for the reply
        var snapshot = await _contextBuilder.BuildContextSnapshotAsync(perceptions, ct, emotionalState).ConfigureAwait(false);

        // Re-search relevant memories using Mark's actual message, not the perception queue.
        // AC1: Use scored search to apply confidence thresholding on cosine similarity.
        // TF-IDF dual search: also search with extracted keywords to find topic-specific
        // memories that casual greeting noise would otherwise bury.
        try
        {
            var scoredResults = await _memory.SearchWithScoresAsync(lastMessage, 5, ct).ConfigureAwait(false);
            var scoredList = scoredResults.ToList();

            // TF-IDF keyword extraction: search with distinctive words to find
            // topic-specific memories ("consulting business" → "Learned Geek")
            // that the full message embedding misses due to casual noise.
            var keywordQuery = await _keywords.ExtractSearchQueryAsync(lastMessage, 5, ct).ConfigureAwait(false);
            if (keywordQuery is not null)
            {
                var keywordResults = await _memory.SearchWithScoresAsync(keywordQuery, 5, ct).ConfigureAwait(false);
                var existingIds = new HashSet<Guid>(scoredList.Select(s => s.Record.Id));
                var newFromKeywords = keywordResults.Where(s => !existingIds.Contains(s.Record.Id)).ToList();
                if (newFromKeywords.Count > 0)
                {
                    _log.LogDebug("TF-IDF dual search: keyword query \"{Query}\" found {Count} additional memories",
                        keywordQuery, newFromKeywords.Count);
                    scoredList.AddRange(newFromKeywords);
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
            var contactName = snapshot.CharacterState.PrimaryContactName ?? "Mark";
            messageRelevant = messageRelevant
                .Where(m => !(m.Content.StartsWith($"{contactName} said:") &&
                             lastMessage.Contains(m.Content[(contactName.Length + 7)..Math.Min(m.Content.Length, contactName.Length + 36)])))
                .Where(m => !m.Content.StartsWith(MemoryPrefixes.ConversationSummary))
                .ToList();

            // Re-rank for diversity against recent thoughts
            var recentThoughts = (await _memory.GetByTypeAsync(MemoryType.InnerThought, 5, ct)
                .ConfigureAwait(false)).ToList();
            messageRelevant = await _contextBuilder.ReRankForDiversityAsync(messageRelevant, recentThoughts, ct)
                .ConfigureAwait(false);

            snapshot.RelevantMemory = messageRelevant;
            _log.LogDebug("Conversation context: re-searched with message text, {Count} relevant memories (confidence floor={Floor:F2})",
                messageRelevant.Count, confidenceFloor);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Conversation memory re-search failed — using perception-based results");
        }

        // Populate RecentHistory with the conversation thread so prompts have full context
        snapshot.RecentHistory = thread.Messages.Select(m => new ChatMessage(
            m.Role == Roles.Ani ? "assistant" : "user",
            m.Content
        )).ToList();

        // Reconsideration path: desire built after choosing silence — skip the decision
        // step (desire already made the call) and use a segue-aware prompt
        if (isReconsideration)
        {
            _log.LogInformation("Reconsideration: desire overrode earlier silence — replying with segue");
            LastEvaluatedMessageAt = null;
        }
        else
        {
            // Step 1: Reply decision (JSON) — should she respond?
            var decisionPrompt = PromptBuilder.BuildReplyDecisionPrompt(snapshot, thread);
            var decisionRaw    = await _ollama.ChatJsonAsync(
                decisionPrompt.System, snapshot.RecentHistory, decisionPrompt.User, ct)
                .ConfigureAwait(false);

            var (shouldReply, reasoning) = ParseReplyDecision(decisionRaw);

            if (!shouldReply)
            {
                // Lock this decision — don't re-evaluate the same message every cycle
                LastEvaluatedMessageAt = thread.Messages[^1].SentAt;
                _log.LogInformation("Reply decision: NO — read it but chose silence. Reasoning: {Reasoning}", reasoning);

                // Persist the silence decision as an inner thought
                var silenceThought = $"I read Mark's message (\"{lastMessage}\") and chose not to reply. {reasoning}";
                await _memory.SaveAsync(new MemoryRecord
                {
                    Type = MemoryType.InnerThought,
                    Content = silenceThought,
                    Importance = 0.5f,
                }, ct).ConfigureAwait(false);

                return;
            }

            // She's replying — clear the gate so future messages evaluate fresh
            LastEvaluatedMessageAt = null;
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
            await _memory.SaveEmotionalStateAsync(emotionalState, ct).ConfigureAwait(false);
        }

        // Feature 10: Receiving Care — detect when contact is checking in on Ani
        if (emotionalState is not null && DetectCareGivingIntent(lastMessage))
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
            var anchorContributions = BuildLexicalAnchorContributions(lastMessage, snapshot.CharacterState);
            if (anchorContributions.Count > 0)
            {
                foreach (var ac in anchorContributions)
                    await _memory.SaveEmotionalContributionAsync(ac, ct).ConfigureAwait(false);

                var allContributions = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
                emotionalState.ComputeFromContributions(allContributions);
                await _memory.SaveEmotionalStateAsync(emotionalState, ct).ConfigureAwait(false);
                _log.LogInformation("Lexical anchors triggered: {Count} — contributions created", anchorContributions.Count);
            }
        }

        // Feature 18: Reactive Withdrawal — detect dismissive or hurtful intent.
        if (emotionalState is not null && DetectHurtIntent(lastMessage))
        {
            _log.LogInformation("Hurt detected in message — creating H1 withdrawal contribution");
            await _emotional.SaveDirectContributionAsync(emotionalState,
                "hurt detected — pulling back emotionally",
                warmth: -0.12f, energy: -0.10f, worry: -0.15f, playfulness: -0.10f,
                ImpactCategory.Conversation, ct).ConfigureAwait(false);

            _withdrawalExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_aniOptions.WithdrawalDurationMinutes);
            _log.LogInformation("Withdrawal active until {Expires}", _withdrawalExpiresAt.Value.ToString("HH:mm"));

            // Save as inner thought so future cycles can reference "something felt off"
            await _memory.SaveAsync(new MemoryRecord
            {
                Type       = MemoryType.InnerThought,
                Content    = "Something in that last message landed in a way that stung a little. I'm still here, just... quieter.",
                Importance = 0.6f,
            }, ct).ConfigureAwait(false);
        }

        // Feature 18: Pass withdrawal state to prompt builder for tone injection
        snapshot.IsWithdrawn = IsWithdrawn;

        // Feature 15 Layer 3: Active contradiction grounding
        await CheckRetrievedMemoryContradictionsAsync(snapshot, lastMessage, ct).ConfigureAwait(false);

        // Feature 14: Bidirectional confidence gate — inbound claim verification.
        if (_aniOptions.ClaimVerificationEnabled && ContainsMemoryReferencingLanguage(lastMessage))
        {
            _log.LogDebug("Feature 14: Memory-referencing language detected — running claim verification");
            await ComputeMarkClaimConfidenceAsync(snapshot, lastMessage, ct).ConfigureAwait(false);
            if (snapshot.MarkClaimNeedsVerification)
            {
                _log.LogInformation("Feature 14: Unverified claims detected ({Count}) — skepticism injection active",
                    snapshot.UnverifiedClaims.Count);
            }
        }

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

        // AC2: Source attribution verification — if Ani's reply references past conversations
        // or shared experiences, verify those claims against actually-retrieved memories.
        // Ungrounded memory claims are the most dangerous form of confabulation.
        if (ContainsMemoryClaimInOutput(reply) && snapshot.RelevantMemory.Count == 0)
        {
            _log.LogWarning("AC2: Reply contains memory claims but no memories were retrieved — re-generating with grounding");
            var cs2 = snapshot.CharacterState;
            var contact2 = cs2.PrimaryContactName ?? "Mark";
            var cleanSystem = $"""
                You are {cs2.Name}. You are in a warm, established relationship with {contact2}.
                Your personality: {string.Join("; ", cs2.CoreTraits)}.

                RULES:
                - Respond naturally to what they just said. This is a real conversation.
                - 1-3 sentences max. Thumb-typed phone text.
                - You have NO memory of past conversations about this topic. That's okay.
                - Be honest — "I don't think we've talked about that" or "wait, tell me more" is warm and real.
                - Do NOT say "remember when" or "you told me about" — you don't have that memory.
                - Do NOT invent details, do NOT narrate what they did, do NOT use third person.
                - Write ONLY the text message. No commentary, no quotation marks.
                """;
            var cleanUser = $"They just said: \"{lastMessage}\"";
            var retryReply = await _ollama.ChatAsync(
                cleanSystem, Array.Empty<ChatMessage>(), cleanUser, ct,
                _ollamaOptions.MemoryGroundedTemperature).ConfigureAwait(false);
            retryReply = CleanOutreachMessage(retryReply);

            if (!string.IsNullOrWhiteSpace(retryReply))
            {
                _log.LogInformation("AC2: Re-generated without memory claims: {Reply}", retryReply);
                reply = retryReply;
            }
        }

        // Echo guard: if the reply is nearly identical to something already in the thread
        // (Ani's prior messages OR Mark's messages), re-generate with a clean slate.
        // Self-echo = model parroting its own context window output.
        // Mark-echo = model parroting the contact's words back instead of engaging.
        var priorMessages = thread.Messages
            .Where(m => m != thread.Messages[^1]) // exclude the message we're replying to from Ani-echo check
            .ToList();
        if (priorMessages.Count > 0)
        {
            try
            {
                var replyEmbedding = await _ollama.EmbedAsync(reply, ct).ConfigureAwait(false);
                foreach (var prior in priorMessages)
                {
                    var priorEmbedding = await _ollama.EmbedAsync(prior.Content, ct).ConfigureAwait(false);
                    var similarity = VectorMath.CosineSimilarity(replyEmbedding, priorEmbedding);

                    // Mark-echo uses a slightly lower threshold — parroting the contact's
                    // exact words is always wrong, even with minor variation
                    var threshold = prior.Role == Roles.Ani ? 0.95f : 0.92f;
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
                        var cs = snapshot.CharacterState;
                        var lastMsg = thread.Messages[^1].Content;
                        var cleanSystem = $"""
                            You are {cs.Name}. You are in a warm, established relationship with {cs.PrimaryContactName ?? "Mark"}.
                            Your personality: {string.Join("; ", cs.CoreTraits)}.

                            RULES:
                            - Respond naturally to what they just said. This is a real conversation.
                            - 1-3 sentences max. Thumb-typed phone text.
                            - You have no memory of this topic coming up before. That's okay.
                            - Be honest — "I don't think you've mentioned that" or "wait, tell me more" is warm and real.
                            - Do NOT invent details, do NOT narrate what they did, do NOT use third person.
                            - Write ONLY the text message. No commentary, no quotation marks.
                            """;
                        var cleanUser = $"They just said: \"{lastMsg}\"";

                        var retryReply = await _ollama.ChatAsync(
                            cleanSystem, Array.Empty<ChatMessage>(), cleanUser, ct)
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

        // Step 4: Send via Twilio
        var decision = new OutreachDecision
        {
            ShouldReach = true,
            Message     = reply,
            ActionType  = ActionTypes.Sms,
            Reasoning   = "conversation reply",
        };
        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);

        // Step 5: Record Ani's reply in the conversation thread
        await _conversations.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role    = Roles.Ani,
            Content = reply,
            SentAt  = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

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
            var related = await _memory.SearchAsync(contactMessage, 3, ct).ConfigureAwait(false);
            foreach (var record in related)
            {
                await _memory.AdjustImportanceAsync(record.Id, 0.1f, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Feedback importance boost failed — continuing without");
        }
    }

    /// Feature 10: Heuristic detection of care-giving intent in a contact's message.
    internal static bool DetectCareGivingIntent(string message)
    {
        var lower = message.ToLowerInvariant().Trim();

        string[] carePatterns =
        [
            "you okay", "you ok", "u okay", "u ok",
            "are you alright", "you alright",
            "how are you", "how're you", "how you doing", "how are you doing",
            "how you feeling", "how are you feeling",
            "checking in", "checking on you", "just checking",
            "everything okay", "everything alright", "everything ok",
            "you good", "u good",
            "what's wrong", "whats wrong",
            "you seem quiet", "you've been quiet", "been quiet",
            "worried about you", "thinking about you",
            "hope you're okay", "hope you're doing",
            "you doing okay", "doing alright",
        ];

        foreach (var pattern in carePatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Feature 18: Heuristic detection of dismissive or hurtful intent in a contact's message.
    /// </summary>
    internal static bool DetectHurtIntent(string message)
    {
        var lower = message.ToLowerInvariant().Trim();

        string[] contextualPatterns =
        [
            "you're just an ai", "you're just a chatbot", "you're a program",
            "none of this is real", "you're not real",
        ];

        foreach (var pattern in contextualPatterns)
        {
            if (!lower.Contains(pattern)) continue;

            if (lower.Contains('?')) return false;

            if (lower.Contains("wonder") || lower.Contains("curious") ||
                lower.Contains("think about") || lower.Contains("sometimes"))
                return false;

            return true;
        }

        string[] directPatterns =
        [
            "you don't actually", "you don't really", "you can't feel",
            "you're fake", "i don't need you",
            "shut up", "you're annoying", "this is stupid", "you're useless",
        ];

        foreach (var pattern in directPatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Feature 19: Scan inbound message for relationship-specific words (lexical anchors)
    /// and build their emotional contributions.
    /// </summary>
    internal static List<EmotionalContribution> BuildLexicalAnchorContributions(
        string message, CharacterStateDoc charState)
    {
        var contributions = new List<EmotionalContribution>();
        if (charState.LexicalAnchors.Count == 0)
            return contributions;

        var lower = message.ToLowerInvariant();
        var (_, halfLife) = ImpactCategoryDefaults.GetDefaults(ImpactCategory.Conversation);

        foreach (var anchor in charState.LexicalAnchors)
        {
            if (!lower.Contains(anchor.Word.ToLowerInvariant()))
                continue;

            var scale = 1.0f;
            if (anchor.DecaysOnRepetition && anchor.TimesHeard > 10)
                scale = Math.Max(0.3f, 1.0f - (anchor.TimesHeard - 10) * 0.03f);

            contributions.Add(new EmotionalContribution
            {
                SourceContent = $"lexical anchor: {anchor.Word}",
                WarmthDelta = anchor.WarmthDelta * scale,
                EnergyDelta = anchor.EnergyDelta * scale,
                WorryDelta = anchor.WorryDelta * scale,
                PlayfulnessDelta = anchor.PlayfulnessDelta * scale,
                HalfLifeHours = halfLife,
                Category = ImpactCategory.Conversation,
            });

            anchor.TimesHeard++;
        }

        return contributions;
    }

    /// Detects whether a message ends with a direct question.
    internal static bool EndsWithDirectQuestion(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        var trimmed = message.Trim();

        var sentences = trimmed.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
        if (sentences.Length == 0) return false;

        var lastSentence = sentences[^1].Trim();
        var lastSentenceEnd = trimmed.LastIndexOf(lastSentence, StringComparison.Ordinal) + lastSentence.Length;

        var trailing = trimmed[lastSentenceEnd..].Trim();

        return trailing.Contains('?') || trimmed[^1] == '?';
    }

    /// <summary>
    /// Detects messages that naturally end a conversation and don't need a reply.
    /// </summary>
    internal static bool IsTerminalMessage(string message)
    {
        var trimmed = message.Trim().ToLowerInvariant();

        // Single emoji or very short emoji-only messages
        if (trimmed.Length <= 4 && !trimmed.Any(char.IsLetter))
            return true;

        string[] terminals =
        [
            "haha", "hahaha", "lol", "lmao", "ok", "okay", "k",
            "goodnight", "good night", "gnight", "nite", "night",
            "ttyl", "talk later", "bye", "gotta go", "👍", "❤️", "😂",
            "🥰", "💕", "😘", "♥️", "👋",
        ];

        return terminals.Contains(trimmed);
    }

    /// <summary>
    /// Feature 14: Lightweight heuristic — does the message contain language that references
    /// past events, attributes statements to Ani, or makes factual claims about the relationship?
    /// </summary>
    internal static bool ContainsMemoryReferencingLanguage(string message)
    {
        var lower = message.ToLowerInvariant();

        string[] patterns =
        [
            "remember when", "remember that", "you said", "you told me",
            "you mentioned", "last time", "you were talking about",
            "didn't you say", "you promised", "you asked me",
            "we talked about", "we discussed", "you brought up",
            "you called me", "you texted me", "earlier you",
            "yesterday you", "the other day you",
        ];

        foreach (var pattern in patterns)
        {
            if (lower.Contains(pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// AC2: Detects whether Ani's reply output contains claims about past conversations
    /// or shared experiences. These are first-person memory claims where Ani asserts she
    /// remembers something — the most dangerous form of confabulation when ungrounded.
    /// </summary>
    internal static bool ContainsMemoryClaimInOutput(string reply)
    {
        var lower = reply.ToLowerInvariant();

        string[] patterns =
        [
            "remember when we", "remember that time", "you told me about",
            "last time we talked", "we were talking about", "you mentioned",
            "that time you", "didn't you tell me", "you said something about",
            "we talked about this", "i remember you", "you brought up",
            "when you told me", "you were saying",
        ];

        foreach (var pattern in patterns)
        {
            if (lower.Contains(pattern))
                return true;
        }

        return false;
    }

    private (bool ShouldReply, string Reasoning) ParseReplyDecision(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw.Trim());
            var shouldReply = doc.RootElement.TryGetProperty("shouldReply", out var sr) && sr.GetBoolean();
            var reasoning = doc.RootElement.TryGetProperty("reasoning", out var r)
                ? r.GetString() ?? "" : "";
            return (shouldReply, reasoning);
        }
        catch
        {
            _log.LogDebug("Reply decision parse failure: {Raw}", raw);
        }

        // Default to replying if we can't parse — better to respond than ignore
        return (true, "");
    }

    /// <summary>
    /// Strips meta-commentary the model adds when roleplaying the act of texting.
    /// </summary>
    private static string? CleanOutreachMessage(string? raw) => Core.Utilities.MessageCleaner.Clean(raw);

    /// <summary>
    /// Feature 15 Layer 3: Check if any retrieved context memories have unresolved contradiction
    /// flags.
    /// </summary>
    private async Task CheckRetrievedMemoryContradictionsAsync(
        ContextSnapshot snapshot, string contactMessage, CancellationToken ct)
    {
        try
        {
            var retrieved = snapshot.RelevantMemory;
            if (retrieved.Count == 0) return;

            var retrievedIds = new HashSet<Guid>(retrieved.Select(m => m.Id));

            var contradictions = await _memory.GetFlaggedContradictionsAsync(includeResolved: false, ct)
                .ConfigureAwait(false);

            var relevant = contradictions
                .Where(c => retrievedIds.Contains(c.NewMemoryId) || retrievedIds.Contains(c.ExistingMemoryId))
                .ToList();

            if (relevant.Count == 0) return;

            foreach (var c in relevant)
            {
                var isNewInContext = retrievedIds.Contains(c.NewMemoryId);
                var contextContent = isNewInContext ? c.NewContent : c.ExistingContent;
                var conflictContent = isNewInContext ? c.ExistingContent : c.NewContent;

                snapshot.ContradictionWarnings.Add(
                    $"Context memory \"{Truncate(contextContent, 60)}\" conflicts with \"{Truncate(conflictContent, 60)}\": {c.Reason}");

                _log.LogInformation(
                    "Feature 15 Layer 3: Contradiction grounding active — context \"{Context}\" vs \"{Conflict}\": {Reason}",
                    Truncate(contextContent, 80), Truncate(conflictContent, 80), c.Reason);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Feature 15 Layer 3: Contradiction check failed — continuing without grounding");
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "\u2026";

    /// <summary>
    /// Feature 14: Extract factual claims from the contact's message and verify them against
    /// episodic memory.
    /// </summary>
    private async Task ComputeMarkClaimConfidenceAsync(
        ContextSnapshot snapshot, string contactMessage, CancellationToken ct)
    {
        try
        {
            var extractionPrompt = PromptBuilder.BuildClaimExtractionPrompt(contactMessage);
            var extractionRaw = await _ollama.ChatJsonAsync(
                extractionPrompt.System, Array.Empty<ChatMessage>(), extractionPrompt.User, ct)
                .ConfigureAwait(false);

            var claims = ParseExtractedClaims(extractionRaw);
            if (claims.Count == 0)
            {
                _log.LogDebug("Feature 14: No verifiable claims extracted from message");
                snapshot.MarkClaimConfidence = 1.0f;
                return;
            }

            var corroboratedCount = 0;
            var unverified = new List<string>();

            foreach (var claim in claims)
            {
                var matches = await _memory.SearchAsync(
                    claim, _aniOptions.ClaimVerificationMaxMemories, ct).ConfigureAwait(false);

                var matchList = matches.ToList();
                if (matchList.Count > 0 && matchList[0].Importance > 0.2f)
                {
                    corroboratedCount++;
                    _log.LogDebug("Feature 14: Claim corroborated — \"{Claim}\"", claim);
                }
                else
                {
                    unverified.Add(claim);
                    _log.LogDebug("Feature 14: Claim NOT corroborated — \"{Claim}\"", claim);
                }
            }

            var confidence = claims.Count > 0 ? (float)corroboratedCount / claims.Count : 1.0f;
            snapshot.MarkClaimConfidence = confidence;
            snapshot.MarkClaimNeedsVerification = confidence < (float)_aniOptions.ClaimVerificationThreshold;
            snapshot.UnverifiedClaims = unverified;

            _log.LogInformation("Feature 14: Claim confidence {Confidence:F2} ({Corroborated}/{Total} claims verified)",
                confidence, corroboratedCount, claims.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Feature 14: Claim verification failed — continuing without skepticism");
            snapshot.MarkClaimConfidence = 1.0f;
        }
    }

    private List<string> ParseExtractedClaims(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw.Trim());
            if (doc.RootElement.TryGetProperty("claims", out var claimsArray) &&
                claimsArray.ValueKind == JsonValueKind.Array)
            {
                return claimsArray.EnumerateArray()
                    .Select(c => c.GetString())
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c!)
                    .ToList();
            }
        }
        catch
        {
            _log.LogDebug("Feature 14: Claim extraction parse failure: {Raw}", raw);
        }

        return new List<string>();
    }
}
