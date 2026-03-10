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
/// Ani's full cognitive cycle, executed once per scheduled wake.
///
/// Phase sequence:
///   1. Perception  — poll all enabled sources since last cycle
///   2. Context     — build snapshot once, share across all phases
///   3. Inner thought — private LLM call; score contact valence; persist
///   4. Desire update — apply temporal drift and trigger weights
///   5. Outreach    — conditional on desire threshold; dispatch or cooldown
///
/// PromptBuilder is stateless and called statically.
/// Perception sources are injected as IEnumerable<IPerceptionSource>.
/// </summary>
public class CognitiveCycleProcessor
{
    private readonly IMemoryService                  _memory;
    private readonly IOllamaClient                   _ollama;
    private readonly DesireEngine                    _desire;
    private readonly AniActionDispatcher             _dispatcher;
    private readonly IConversationService            _conversations;
    private readonly IEnumerable<IPerceptionSource>  _sources;
    private readonly AdminCommandHandler             _adminCommands;
    private readonly AniOptions                      _aniOptions;
    private readonly ILogger<CognitiveCycleProcessor> _log;

    private DateTimeOffset _lastCycleAt = DateTimeOffset.UtcNow;

    // Tracks the last contact message we evaluated a reply decision for.
    // Once Ani decides "NO" on a specific message, she won't re-evaluate it every cycle.
    // Resets when a new message arrives (different SentAt timestamp).
    private DateTimeOffset? _lastEvaluatedMessageAt;

    // Reactive share rate limiting — resets daily
    private int  _reactiveShareCount;
    private DateTimeOffset _reactiveShareDay = DateTimeOffset.MinValue;

    // Dedup cache: prevents saving the same perception (e.g. "probably at the gym")
    // every cycle. Key = summary text, Value = when it was last persisted.
    private readonly Dictionary<string, DateTimeOffset> _recentPerceptions = new();
    private static readonly TimeSpan PerceptionDedupeWindow = TimeSpan.FromHours(4);

    public CognitiveCycleProcessor(
        IMemoryService                 memory,
        IOllamaClient                  ollama,
        DesireEngine                   desire,
        AniActionDispatcher            dispatcher,
        IConversationService           conversations,
        IEnumerable<IPerceptionSource> sources,
        AdminCommandHandler            adminCommands,
        IOptions<AniOptions>           aniOptions,
        ILogger<CognitiveCycleProcessor> log)
    {
        _memory        = memory;
        _ollama        = ollama;
        _desire        = desire;
        _dispatcher    = dispatcher;
        _conversations = conversations;
        _sources       = sources;
        _adminCommands = adminCommands;
        _aniOptions    = aniOptions.Value;
        _log           = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogDebug("Cognitive cycle starting");

        // Phase 0: Emotional state — load and drift toward baselines
        var emotionalState = await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var elapsed = DateTimeOffset.UtcNow - emotionalState.LastUpdated;
        emotionalState.DriftTowardBaseline(elapsed);
        await _memory.SaveEmotionalStateAsync(emotionalState, ct).ConfigureAwait(false);

        // Phase 1: Perception (includes Twilio inbound polling + conversation timeout checks)
        var perceptions = await PollPerceptionSourcesAsync(ct).ConfigureAwait(false);

        // Persist notable perceptions so they accumulate embeddings and feed future
        // semantic search. Without this, perceptions are ephemeral — gone after one cycle.
        await PersistNotablePerceptionsAsync(perceptions, ct).ConfigureAwait(false);

        // Load character state early — needed for dynamic name references in logs and memory
        var charState = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);

        // Phase 2: Check for active conversation — if contact texted, route to reply mode
        var activeThread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
        var hasUnreadFromContact = activeThread?.Messages.Count > 0 &&
                                   activeThread.Messages[^1].Role == "mark";

        if (hasUnreadFromContact)
        {
            // Record inbound contact — feeds desire drift timing
            await _desire.RecordInboundContactAsync(ct).ConfigureAwait(false);

            var lastMsg = activeThread!.Messages[^1];

            // Admin commands bypass conversation entirely — handle and exit
            if (AdminCommandHandler.IsAdminCommand(lastMsg.Content))
            {
                _log.LogInformation("Admin command detected: {Content}", lastMsg.Content);
                await _adminCommands.HandleAsync(lastMsg.Content, ct).ConfigureAwait(false);

                // Close the thread so the admin command doesn't linger as "unread"
                await _conversations.CloseThreadAsync(activeThread.Id, ct).ConfigureAwait(false);
                _lastCycleAt = DateTimeOffset.UtcNow;
                return;
            }

            // If we already evaluated this exact message and decided NO, don't re-ask.
            // A new message from the contact (different SentAt) resets the gate.
            if (_lastEvaluatedMessageAt.HasValue && lastMsg.SentAt == _lastEvaluatedMessageAt.Value)
            {
                _log.LogDebug("Already evaluated reply for message at {SentAt} — skipping to ambient mode",
                    lastMsg.SentAt);
            }
            else
            {
                _log.LogInformation("Conversation mode — {Contact}'s last message: {Message}",
                    charState.PrimaryContactName, lastMsg.Content);
                await RunConversationReplyAsync(activeThread, perceptions, ct, emotionalState).ConfigureAwait(false);
                _lastCycleAt = DateTimeOffset.UtcNow;
                return;
            }
        }

        // Phase 3: Reactive sharing — high-relevance RSS items shared directly
        // Bypasses desire engine but respects daily rate limit and cooldown
        if (await TryReactiveShareAsync(perceptions, charState, ct).ConfigureAwait(false))
        {
            _lastCycleAt = DateTimeOffset.UtcNow;
            return;
        }

        // Phase 4: Context snapshot — built once, shared across all ambient phases
        var snapshot = await BuildContextSnapshotAsync(perceptions, ct, emotionalState).ConfigureAwait(false);

        // Phase 4: Inner thought
        var (thought, valence) = await RunInnerThoughtAsync(snapshot, ct).ConfigureAwait(false);

        await _memory.SaveAsync(new MemoryRecord
        {
            Type        = MemoryType.InnerThought,
            Content     = thought,
            ContactValence = valence,
            Importance  = valence > (float)_aniOptions.ValenceTriggerThreshold ? 0.8f : 0.3f,
            OccurredAt  = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        _log.LogInformation("Inner thought (valence={Valence:F2}): {Thought}",
            valence, thought);

        // Phase 4b: Emotional shift from inner thought
        await ApplyEmotionalShiftAsync(emotionalState, thought, ct).ConfigureAwait(false);

        // Phase 5: Desire update
        await _desire.ApplyDriftAsync(ct).ConfigureAwait(false);

        if (valence > (float)_aniOptions.ValenceTriggerThreshold)
            await _desire.AddTriggerAsync(
                TriggerType.SpontaneousThought, valence,
                $"thought: {thought[..Math.Min(60, thought.Length)]}", ct).ConfigureAwait(false);

        // Phase 6: Outreach — only if desire crosses threshold
        // Suppress outreach while a conversation is active — contact just texted recently,
        // sending unrelated ambient thoughts would be jarring and disjointed
        if (activeThread is not null)
        {
            _log.LogDebug("Active conversation — suppressing ambient outreach");
            _lastCycleAt = DateTimeOffset.UtcNow;
            return;
        }

        if (!await _desire.ShouldReachOutAsync(ct).ConfigureAwait(false))
        {
            _log.LogDebug("Desire below threshold — no outreach this cycle");
            _lastCycleAt = DateTimeOffset.UtcNow;
            return;
        }

        await RunOutreachAsync(snapshot, thought, ct).ConfigureAwait(false);
        _lastCycleAt = DateTimeOffset.UtcNow;
    }

    // ── Private phases ────────────────────────────────────────────────────────

    private async Task<List<PerceptionEvent>> PollPerceptionSourcesAsync(CancellationToken ct)
    {
        var events = new List<PerceptionEvent>();

        foreach (var source in _sources.Where(s => s.IsEnabled))
        {
            try
            {
                var polled = await source.PollAsync(_lastCycleAt, ct).ConfigureAwait(false);
                events.AddRange(polled);
            }
            catch (Exception ex)
            {
                // A failing perception source must not kill the cognitive cycle
                _log.LogWarning(ex, "Perception source '{Source}' failed — skipping", source.SourceName);
            }
        }

        return events;
    }

    /// <summary>
    /// Saves notable perceptions (RSS articles, mark-state inferences) as memory records
    /// so they get embedded and become findable via semantic search in future cycles.
    /// Low-relevance or time-only perceptions are skipped to avoid noise.
    /// </summary>
    private async Task PersistNotablePerceptionsAsync(
        List<PerceptionEvent> perceptions, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Evict stale entries from the dedup cache
        var stale = _recentPerceptions
            .Where(kv => now - kv.Value > PerceptionDedupeWindow)
            .Select(kv => kv.Key).ToList();
        foreach (var key in stale) _recentPerceptions.Remove(key);

        foreach (var p in perceptions)
        {
            // Skip low-relevance perceptions (e.g. "It's 3:14 PM on a Tuesday")
            // and time-source events which are always regenerated fresh
            if (p.ContactRelevance < 0.25f || p.SourceName == "time")
                continue;

            // Skip if we recently saved an identical perception
            if (_recentPerceptions.ContainsKey(p.Summary))
                continue;

            try
            {
                await _memory.SaveAsync(new MemoryRecord
                {
                    Type        = MemoryType.Perception,
                    Content     = p.Summary,
                    ContactValence = p.ContactRelevance,
                    Importance  = p.ContactRelevance,
                    SourceName  = p.SourceName,
                    OccurredAt  = p.OccurredAt,
                }, ct).ConfigureAwait(false);

                _recentPerceptions[p.Summary] = now;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to persist perception from {Source}", p.SourceName);
            }
        }
    }

    /// <summary>
    /// Checks for high-relevance RSS items that the contact would care about and shares
    /// them directly — bypassing the desire engine. Rate-limited to prevent spam.
    /// Returns true if a share was sent (cycle should end), false otherwise.
    /// </summary>
    private async Task<bool> TryReactiveShareAsync(
        List<PerceptionEvent> perceptions, CharacterStateDoc charState, CancellationToken ct)
    {
        var threshold = (float)_aniOptions.ReactiveShareThreshold;
        var shareable = perceptions
            .Where(p => p.SourceName == "rss" && p.ContactRelevance >= threshold)
            .OrderByDescending(p => p.ContactRelevance)
            .FirstOrDefault();

        if (shareable is null)
            return false;

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

        // Respect a shorter cooldown for reactive shares — news shouldn't wait 60 min,
        // but she also shouldn't share 2 minutes after texting
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

        // Generate the share message
        var prompt = PromptBuilder.BuildReactiveSharePrompt(charState, shareable.Summary);
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

        await _memory.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = $"{charState.Name} shared with {charState.PrimaryContactName}: {message} (about: {shareable.Summary})",
            Importance = 0.5f,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        return true;
    }

    private async Task<ContextSnapshot> BuildContextSnapshotAsync(
        List<PerceptionEvent> perceptions, CancellationToken ct,
        EmotionalState? emotionalState = null)
    {
        var charState    = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);
        var desireState  = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        var recentEpisodic = await _memory.GetByTypeAsync(MemoryType.Episodic, 10, ct).ConfigureAwait(false);
        var recentThoughts = await _memory.GetByTypeAsync(MemoryType.InnerThought, 5, ct).ConfigureAwait(false);
        var recentMem    = recentEpisodic.Concat(recentThoughts).ToList();
        var openLoops    = await _memory.GetOpenLoopsAsync(ct).ConfigureAwait(false);

        // Semantic search: use perceptions as the query to surface memories relevant
        // to what Ani is currently experiencing — not just the most recent ones
        var relevantMem = new List<MemoryRecord>();
        if (perceptions.Count > 0)
        {
            var searchQuery = string.Join(". ", perceptions.Select(p => p.Summary));
            try
            {
                var results = await _memory.SearchAsync(searchQuery, 5, ct).ConfigureAwait(false);
                relevantMem = results.ToList();
                _log.LogDebug("Semantic search returned {Count} relevant memories", relevantMem.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Semantic memory search failed — continuing without");
            }
        }

        // Extract recent conversation summary — the most important context for what's
        // happening in the contact's life right now. This feeds into inner thoughts,
        // outreach decisions, and outreach messages.
        var conversationSummary = recentEpisodic
            .Where(m => m.Content.StartsWith("Conversation ("))
            .Select(m => m.Content)
            .FirstOrDefault();

        // Thought loop detection via semantic search — find recent inner thoughts that
        // are similar to the current context. If similarity is high, the model is stuck
        // in a loop and needs stronger diversity signals.
        var similarThoughts = new List<MemoryRecord>();
        if (perceptions.Count > 0)
        {
            var thoughtQuery = string.Join(". ", perceptions.Select(p => p.Summary));
            try
            {
                var results = await _memory.SearchByTypeAsync(
                    thoughtQuery, MemoryType.InnerThought, 3, ct).ConfigureAwait(false);
                similarThoughts = results.ToList();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Thought similarity search failed — continuing without");
            }
        }

        emotionalState ??= await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);

        return new ContextSnapshot
        {
            CharacterState           = charState,
            DesireState              = desireState,
            EmotionalState           = emotionalState,
            RecentMemory             = recentMem.ToList(),
            RelevantMemory           = relevantMem,
            OpenLoops                = openLoops.ToList(),
            Perceptions              = perceptions,
            BuiltAt                  = DateTimeOffset.UtcNow,
            RecentConversationSummary = conversationSummary,
            SimilarRecentThoughts    = similarThoughts,
        };
    }

    private async Task<(string thought, float valence)> RunInnerThoughtAsync(
        ContextSnapshot snapshot, CancellationToken ct)
    {
        var thoughtPrompt = PromptBuilder.BuildInnerThoughtPrompt(snapshot);
        var thought       = await _ollama.InnerMonologueChatAsync(
            thoughtPrompt.System, snapshot.RecentHistory, thoughtPrompt.User, ct)
            .ConfigureAwait(false);

        var valence = await ScoreContactValenceAsync(thought, snapshot.CharacterState, ct)
            .ConfigureAwait(false);

        return (thought, valence);
    }

    private async Task<float> ScoreContactValenceAsync(
        string thought, CharacterStateDoc character, CancellationToken ct)
    {
        var prompt = PromptBuilder.BuildValenceScoringPrompt(thought, character);
        var raw    = await _ollama.ChatJsonAsync(
            prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
            .ConfigureAwait(false);

        return ParseValenceScore(raw);
    }

    private async Task RunOutreachAsync(
        ContextSnapshot snapshot, string recentThought, CancellationToken ct)
    {
        // Step 1: Decision — should Ani reach out? (JSON, no message required)
        var outreachPrompt = PromptBuilder.BuildOutreachPrompt(snapshot, recentThought);
        var raw            = await _ollama.ChatJsonAsync(
            outreachPrompt.System, snapshot.RecentHistory, outreachPrompt.User, ct)
            .ConfigureAwait(false);

        var decision = ParseOutreachDecision(raw);
        _log.LogDebug("Outreach decision raw: {Raw}", raw);

        if (!decision.ShouldReach)
        {
            // Genuine "no" — she considered it but chose not to. No cooldown.
            // Instead, bump desire slightly — the "I want to but not yet" builds tension.
            _log.LogInformation("Outreach decision: NO (confidence={Confidence:F2}) — {Reasoning}",
                decision.Confidence, decision.Reasoning);
            await _desire.AddTriggerAsync(
                TriggerType.SpontaneousThought, 0.3f,
                "considered reaching out but held back", ct).ConfigureAwait(false);
            return;
        }

        // Step 2: Compose — free-text message generation (no JSON constraint)
        var msgPrompt = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, recentThought, decision.Reasoning ?? string.Empty);
        var message = await _ollama.ChatAsync(
            msgPrompt.System, snapshot.RecentHistory, msgPrompt.User, ct)
            .ConfigureAwait(false);

        message = CleanOutreachMessage(message);
        _log.LogInformation("Outreach message composed: {Message}", message);

        if (string.IsNullOrWhiteSpace(message))
        {
            _log.LogWarning("Outreach message was empty after composition — retrying next opportunity");
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            return;
        }

        // Step 3: Light pronoun fix — only if third-person leaked through
        var rewritten = await FixPronounsIfNeeded(message, snapshot.CharacterState, ct)
            .ConfigureAwait(false);

        decision.Message    = rewritten;
        decision.ActionType = "sms";

        var cs = snapshot.CharacterState;
        _log.LogInformation("{Name} reaching out: {Message}", cs.Name, decision.Message);

        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);
        await _desire.ResetAfterOutreachAsync(ct).ConfigureAwait(false);

        await _memory.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = $"{cs.Name} reached out: {decision.Message}",
            Importance = 0.7f,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Conversation mode: contact texted and their message is the last in the thread.
    /// Decides whether to reply, and if so, generates and sends a contextual response.
    ///
    /// Three no-reply conditions (baked in from day one):
    ///   1. Last message is Ani's — conversation is already "answered"
    ///   2. Terminal message detected (lol, haha, goodnight, emoji-only)
    ///   3. Model decides no reply needed (genuine silence)
    /// </summary>
    private async Task RunConversationReplyAsync(
        ConversationThread thread, List<PerceptionEvent> perceptions, CancellationToken ct,
        EmotionalState? emotionalState = null)
    {
        var lastMessage = thread.Messages[^1].Content;

        // Check 1: is this a terminal message that doesn't need a reply?
        if (IsTerminalMessage(lastMessage))
        {
            _log.LogInformation("Terminal message detected (\"{Message}\") — no reply needed", lastMessage);
            return;
        }

        // Build context for the reply
        var snapshot = await BuildContextSnapshotAsync(perceptions, ct, emotionalState).ConfigureAwait(false);

        // Populate RecentHistory with the conversation thread so prompts have full context
        snapshot.RecentHistory = thread.Messages.Select(m => new ChatMessage(
            m.Role == "ani" ? "assistant" : "user",
            m.Content
        )).ToList();

        // Step 1: Reply decision (JSON) — should she respond?
        var decisionPrompt = PromptBuilder.BuildReplyDecisionPrompt(snapshot, thread);
        var decisionRaw    = await _ollama.ChatJsonAsync(
            decisionPrompt.System, snapshot.RecentHistory, decisionPrompt.User, ct)
            .ConfigureAwait(false);

        var shouldReply = ParseReplyDecision(decisionRaw);

        if (!shouldReply)
        {
            // Lock this decision — don't re-evaluate the same message every cycle
            _lastEvaluatedMessageAt = thread.Messages[^1].SentAt;
            _log.LogInformation("Reply decision: NO — read it but chose silence");
            return;
        }

        // She's replying — clear the gate so future messages evaluate fresh
        _lastEvaluatedMessageAt = null;

        // Step 2: Generate reply (free text, using conversation model)
        var replyPrompt = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);
        var reply = await _ollama.ChatAsync(
            replyPrompt.System, snapshot.RecentHistory, replyPrompt.User, ct)
            .ConfigureAwait(false);

        reply = CleanOutreachMessage(reply);
        _log.LogInformation("Conversation reply: {Reply}", reply);

        if (string.IsNullOrWhiteSpace(reply))
        {
            _log.LogWarning("Conversation reply was empty — skipping");
            return;
        }

        // Step 3: Send via Twilio
        var decision = new OutreachDecision
        {
            ShouldReach = true,
            Message     = reply,
            ActionType  = ActionTypes.Sms,
            Reasoning   = "conversation reply",
        };
        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);

        // Step 4: Record Ani's reply in the conversation thread
        await _conversations.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role    = "ani",
            Content = reply,
            SentAt  = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        // Update desire — contact happened
        await _desire.ResetAfterOutreachAsync(ct).ConfigureAwait(false);

        // Emotional shift from conversation — receiving a message + replying is emotionally warm
        if (emotionalState is not null)
        {
            var cs = snapshot.CharacterState;
            var conversationContext = $"{cs.PrimaryContactName} said: \"{lastMessage}\" and {cs.Name} replied: \"{reply}\"";
            await ApplyEmotionalShiftAsync(emotionalState, conversationContext, ct, maxDelta: 0.4f).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Detects messages that naturally end a conversation and don't need a reply.
    /// "haha", "lol", heart emoji, "goodnight", "ttyl", "ok", single emoji, etc.
    /// </summary>
    private static bool IsTerminalMessage(string message)
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

    private bool ParseReplyDecision(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw.Trim());
            if (doc.RootElement.TryGetProperty("shouldReply", out var sr))
                return sr.GetBoolean();
        }
        catch
        {
            _log.LogDebug("Reply decision parse failure: {Raw}", raw);
        }

        // Default to replying if we can't parse — better to respond than ignore
        return true;
    }

    /// <summary>
    /// Strips meta-commentary the model adds when roleplaying the act of texting.
    /// The actual message is always the first paragraph; everything after a blank line
    /// is the model reviewing/explaining its own work.
    /// </summary>
    private static string? CleanOutreachMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var cleaned = raw.Trim().Trim('"');

        // Take only the first paragraph — model puts meta-commentary after blank lines
        var doubleNewline = cleaned.IndexOf("\n\n", StringComparison.Ordinal);
        if (doubleNewline > 0)
            cleaned = cleaned[..doubleNewline].Trim();

        // Also catch single-newline commentary patterns like "that's the..." or "that's perfect..."
        var lines = cleaned.Split('\n');
        var messageParts = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("that's ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("this is ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("i'm keeping it", StringComparison.OrdinalIgnoreCase))
                break; // meta-commentary starts here
            messageParts.Add(trimmed);
        }
        cleaned = string.Join("\n", messageParts).Trim();

        // Remove trailing meta-commentary patterns
        string[] trailingJunk = ["sent.", "your turn.", "(waiting)", "now wait for a reply...", "i can do this."];
        bool changed;
        do
        {
            changed = false;
            foreach (var junk in trailingJunk)
            {
                if (cleaned.EndsWith(junk, StringComparison.OrdinalIgnoreCase))
                {
                    cleaned = cleaned[..^junk.Length].TrimEnd('\n', '\r', ' ');
                    changed = true;
                }
            }
        } while (changed);

        // Hard cap: keep only the first 2 sentences — model ignores "1-2 sentences" in prompts
        cleaned = TruncateToSentences(cleaned, maxSentences: 2);

        return string.IsNullOrWhiteSpace(cleaned) ? raw.Trim() : cleaned;
    }

    /// <summary>
    /// Keeps only the first N sentences from a message.
    /// Sentence boundaries: '.', '!', '?' followed by whitespace or end-of-string.
    /// Preserves trailing ellipsis (…, ...) without counting as a sentence end.
    /// </summary>
    private static string TruncateToSentences(string text, int maxSentences)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is not ('.' or '!' or '?')) continue;

            // Skip ellipsis patterns (... or …)
            if (ch == '.' && i + 1 < text.Length && text[i + 1] == '.') continue;

            // Must be followed by whitespace or end-of-string to count as sentence end
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) continue;

            count++;
            if (count >= maxSentences)
                return text[..(i + 1)].Trim();
        }

        return text; // fewer sentences than max — return as-is
    }

    /// <summary>
    /// Light pronoun fix — only invoked if the message actually contains third-person references.
    /// Avoids the rewrite pass completely when the message is already correct, which prevents
    /// the model from "creatively improving" a perfectly good text into poetic nonsense.
    ///
    /// Safety: if the rewrite changes the message length by more than 50%, the model went
    /// creative instead of just fixing pronouns — fall back to the original.
    /// </summary>
    private async Task<string> FixPronounsIfNeeded(
        string message, CharacterStateDoc character, CancellationToken ct)
    {
        // Quick check: does the message even contain third-person pronouns?
        var lower = message.ToLowerInvariant();
        var hasThirdPerson = lower.Contains(" him") || lower.Contains(" his ") ||
                             lower.Contains(" he ") || lower.StartsWith("he ") ||
                             lower.Contains("him.") || lower.Contains("his.");

        if (!hasThirdPerson)
        {
            _log.LogDebug("Outreach message already in second person — skipping rewrite");
            return message;
        }

        var system = """
            Fix ONLY the pronouns in this text message. Change "he"/"him"/"his" to "you"/"your".
            Do NOT change anything else. Do NOT add words, commentary, or rewrite the message.
            Return ONLY the fixed message text — same words, same length, just pronouns swapped.
            """;

        var rewritten = await _ollama.ChatAsync(system, Array.Empty<ChatMessage>(), message, ct)
            .ConfigureAwait(false);

        rewritten = CleanOutreachMessage(rewritten);

        // Safety check: if the rewrite is too different, the model rewrote instead of fixing
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

    /// <summary>
    /// Scores and applies emotional shift from a thought, conversation reply, or event.
    /// Uses the LLM to extract small deltas for each emotional dimension.
    ///
    /// maxDelta controls the clamp range:
    ///   0.2 = routine inner thoughts (default)
    ///   0.4 = conversations with contact (real emotional events)
    /// </summary>
    private async Task ApplyEmotionalShiftAsync(
        EmotionalState state, string content, CancellationToken ct, float maxDelta = 0.2f)
    {
        try
        {
            var prompt = PromptBuilder.BuildEmotionalShiftPrompt(content, state, maxDelta);
            var raw = await _ollama.ChatJsonAsync(
                prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
                .ConfigureAwait(false);

            var (warmth, energy, concern, playfulness) = ParseEmotionalShift(raw, maxDelta);
            state.ApplyShift(warmth, energy, concern, playfulness);

            _log.LogDebug("Emotional shift (max={MaxDelta:F1}): W={Warmth:+0.00;-0.00} E={Energy:+0.00;-0.00} C={Concern:+0.00;-0.00} P={Playfulness:+0.00;-0.00}",
                maxDelta, warmth, energy, concern, playfulness);

            await _memory.SaveEmotionalStateAsync(state, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Emotional shift scoring failed — continuing with current state");
        }
    }

    private (float warmth, float energy, float concern, float playfulness) ParseEmotionalShift(string raw, float maxDelta = 0.2f)
    {
        try
        {
            var doc = JsonDocument.Parse(raw.Trim());
            var root = doc.RootElement;
            return (
                ClampDelta(root, "warmth"),
                ClampDelta(root, "energy"),
                ClampDelta(root, "concern"),
                ClampDelta(root, "playfulness")
            );
        }
        catch
        {
            _log.LogDebug("Emotional shift parse failure: {Raw}", raw);
            return (0f, 0f, 0f, 0f);
        }

        float ClampDelta(JsonElement root, string prop)
        {
            if (root.TryGetProperty(prop, out var val))
                return (float)Math.Clamp(val.GetDouble(), -maxDelta, maxDelta);
            return 0f;
        }
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static float ParseValenceScore(string raw)
    {
        try
        {
            var doc   = JsonDocument.Parse(raw.Trim());
            var score = doc.RootElement.GetProperty("score").GetDouble();
            return (float)Math.Clamp(score, 0.0, 1.0);
        }
        catch
        {
            // Unparseable valence defaults to neutral — not a fatal failure
            return 0.3f;
        }
    }

    private OutreachDecision ParseOutreachDecision(string raw)
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

            // triggersActedOn can be strings OR objects — handle both gracefully
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
}
