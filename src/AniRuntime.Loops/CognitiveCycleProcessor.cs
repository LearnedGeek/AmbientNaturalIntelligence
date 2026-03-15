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
    // Public so the heartbeat can check whether the contact's last message has already
    // been evaluated — if so, use ambient timing instead of conversation heartbeat.
    public DateTimeOffset? LastEvaluatedMessageAt { get; private set; }

    // Reactive share rate limiting — resets daily
    private int  _reactiveShareCount;
    private DateTimeOffset _reactiveShareDay = DateTimeOffset.MinValue;

    // Feature 18: Reactive withdrawal — transient emotional state after hurt detection.
    // Suppresses outreach and injects quieter tone during the withdrawal window.
    private DateTimeOffset? _withdrawalExpiresAt;
    internal bool IsWithdrawn => _withdrawalExpiresAt.HasValue && DateTimeOffset.UtcNow < _withdrawalExpiresAt.Value;

    // Feature 3: Rate-limit silence choice recording — once per 4 hours max
    private DateTimeOffset _lastSilenceRecordedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan SilenceRecordCooldown = TimeSpan.FromHours(4);

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

        // Phase 0: Emotional state — compute from active contributions.
        // Each contribution decays independently via its own half-life.
        // The emotional state is baselines + sum of all decayed contributions.
        var emotionalState = await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var activeContributions = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
        emotionalState.ComputeFromContributions(activeContributions);

        // Periodic cleanup of fully-decayed contributions (> 24h old)
        await _memory.CleanupDecayedContributionsAsync(ct).ConfigureAwait(false);

        // Feature 2: Open loops as emotional weight — unresolved threads create gentle
        // concern pressure. Proportional to count and age of oldest loop.
        await ApplyOpenLoopPressureAsync(emotionalState, ct).ConfigureAwait(false);

        // Feature 17: Contact-gap tension — relational ache builds during prolonged absence.
        // Uses LastContactInbound from desire state to track how long since contact reached out.
        var desireForTension = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        var lastContact = desireForTension.LastContactInbound;
        if (lastContact != default)
        {
            var hoursSinceContact = (DateTimeOffset.UtcNow - lastContact).TotalHours;
            var previousTension = emotionalState.ContactGapTension;
            emotionalState.AccumulateContactGapTension(
                hoursSinceContact,
                _aniOptions.TensionOnsetHours,
                _aniOptions.TensionAccumulationRate,
                _aniOptions.TensionMax);
            if (emotionalState.ContactGapTension != previousTension)
            {
                _log.LogDebug("Contact-gap tension: {Previous:F3} → {New:F3} (hours since contact: {Hours:F1})",
                    previousTension, emotionalState.ContactGapTension, hoursSinceContact);
            }
        }

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

                // Close the thread FIRST so the command doesn't replay if the reply fails
                await _conversations.CloseThreadAsync(activeThread.Id, ct).ConfigureAwait(false);

                await _adminCommands.HandleAsync(lastMsg.Content, ct).ConfigureAwait(false);
                _lastCycleAt = DateTimeOffset.UtcNow;
                return;
            }

            // If we already evaluated this exact message and decided NO, don't re-ask.
            // A new message from the contact (different SentAt) resets the gate.
            if (LastEvaluatedMessageAt.HasValue && lastMsg.SentAt == LastEvaluatedMessageAt.Value)
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

        // Phase 4: Inner thought + reflection
        var (thought, reflection, valence) = await RunInnerThoughtAsync(snapshot, ct).ConfigureAwait(false);

        // Store thought with reflection appended — keeps inner life richness in memory
        var contentForStorage = reflection is not null
            ? $"{thought} [reflection: {reflection}]"
            : thought;

        await _memory.SaveAsync(new MemoryRecord
        {
            Type        = MemoryType.InnerThought,
            Content     = contentForStorage,
            RelationalValence = valence,
            Importance  = valence > (float)_aniOptions.ValenceTriggerThreshold ? 0.8f : 0.3f,
            OccurredAt  = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        _log.LogInformation("Inner thought (valence={Valence:F2}): {Thought}",
            valence, thought);
        if (reflection is not null)
            _log.LogInformation("Reflection: {Reflection}", reflection);

        // Phase 4b: Emotional shift from inner thought — creates an Ambient contribution
        // that decays via 1-hour half-life. Each thought's impact fades independently.
        await ApplyEmotionalShiftAsync(emotionalState, thought, ct,
            isAmbientCycle: true, category: ImpactCategory.Ambient).ConfigureAwait(false);

        // Phase 5: Desire update
        await _desire.ApplyDriftAsync(ct).ConfigureAwait(false);

        if (valence > (float)_aniOptions.ValenceTriggerThreshold)
            await _desire.AddTriggerAsync(
                TriggerType.SpontaneousThought, valence,
                $"thought: {thought[..Math.Min(60, thought.Length)]}", ct).ConfigureAwait(false);

        // Phase 6: Outreach — only if desire crosses threshold
        // If a conversation is active and she already chose silence, but desire has built
        // high enough, allow her to reconsider — a natural "wait, one more thing" moment.
        // Otherwise suppress ambient outreach during active conversations.
        if (activeThread is not null)
        {
            if (hasUnreadFromContact && LastEvaluatedMessageAt.HasValue &&
                await _desire.ShouldReachOutAsync(ct).ConfigureAwait(false))
            {
                _log.LogInformation("Desire built after choosing silence — reconsidering reply");
                await RunConversationReplyAsync(activeThread, perceptions, ct, emotionalState,
                    isReconsideration: true).ConfigureAwait(false);
            }
            else
            {
                _log.LogDebug("Active conversation — suppressing ambient outreach");
            }
            _lastCycleAt = DateTimeOffset.UtcNow;
            return;
        }

        // Feature 18: Suppress outreach during emotional withdrawal
        if (IsWithdrawn)
        {
            _log.LogInformation("Outreach suppressed: withdrawal active (expires {Expires})",
                _withdrawalExpiresAt!.Value.ToString("HH:mm"));
            _lastCycleAt = DateTimeOffset.UtcNow;
            return;
        }

        if (!await _desire.ShouldReachOutAsync(ct).ConfigureAwait(false))
        {
            _log.LogDebug("Desire below threshold — no outreach this cycle");

            // Feature 3: Silence as active system — when desire is notable (>0.3) but
            // below threshold, the choice not to speak is meaningful. Generate a brief
            // inner thought about choosing silence. Rate-limited to once per 4 hours.
            var desireState = await _desire.GetStateAsync(ct).ConfigureAwait(false);
            if (desireState.DesireToConnect > 0.3f)
            {
                await RecordSilenceChoiceAsync(desireState, emotionalState, ct).ConfigureAwait(false);
            }

            _lastCycleAt = DateTimeOffset.UtcNow;
            return;
        }

        // Feature 27: Hard runtime gates — outreach continuity checks
        var outreachCtx = snapshot.OutreachContext;
        if (outreachCtx is not null)
        {
            if (outreachCtx.UnansweredCount >= _aniOptions.MaxUnansweredBeforeSilence)
            {
                _log.LogInformation(
                    "Outreach suppressed: {Count} unanswered messages (limit={Limit}) — waiting for reply",
                    outreachCtx.UnansweredCount, _aniOptions.MaxUnansweredBeforeSilence);
                _lastCycleAt = DateTimeOffset.UtcNow;
                return;
            }

            if (outreachCtx.TimeSinceLastSend.HasValue &&
                outreachCtx.TimeSinceLastSend.Value.TotalMinutes < _aniOptions.MinSendGapMinutes)
            {
                _log.LogInformation(
                    "Outreach suppressed: only {Minutes:F0} min since last send (minimum={Gap} min)",
                    outreachCtx.TimeSinceLastSend.Value.TotalMinutes, _aniOptions.MinSendGapMinutes);
                _lastCycleAt = DateTimeOffset.UtcNow;
                return;
            }
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
                    RelationalValence = p.ContactRelevance,
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

        // Generate the share message (with mood coloring)
        var currentMood = await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);
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

        // Feature 16: Load anchored (foundation) memories — always present in context
        var anchoredMemories = new List<MemoryRecord>();
        try
        {
            anchoredMemories = (await _memory.GetAnchoredMemoriesAsync(ct).ConfigureAwait(false)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load anchored memories — continuing without");
        }

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

        // Topic-weighted diversity: rerank relevant memories to prefer topics that are
        // dissimilar from recent inner thoughts. This steers the model toward fresh topics
        // by changing what context it sees — not by telling it what to avoid.
        // Uses embeddings (not text injection) per design decision.
        relevantMem = await ReRankForDiversityAsync(
            relevantMem, recentThoughts.ToList(), ct).ConfigureAwait(false);

        emotionalState ??= await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);

        // Feature 27: Assemble recent outreach context for continuity awareness
        var outreachContext = BuildOutreachContext(recentMem, desireState, charState);

        // Feature 4: Load relationship health — updated at most once per day
        RelationshipHealth? relationshipHealth = null;
        try
        {
            relationshipHealth = await _memory.GetRelationshipHealthAsync(ct).ConfigureAwait(false);

            // Recalculate if stale (>24h since last calculation)
            if ((DateTimeOffset.UtcNow - relationshipHealth.LastCalculated).TotalHours >= 24)
            {
                relationshipHealth = await ComputeRelationshipHealthAsync(
                    relationshipHealth, emotionalState, ct).ConfigureAwait(false);
                await _memory.SaveRelationshipHealthAsync(relationshipHealth, ct).ConfigureAwait(false);
                _log.LogInformation("Relationship health recalculated: score={Score:F2}, phase={Phase}",
                    relationshipHealth.ConnectionScore, relationshipHealth.Phase);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load/compute relationship health — continuing without");
        }

        // Feature 8: Emotional drift detection — compare recent vs older emotional vectors
        EmotionalDrift? emotionalDrift = null;
        try
        {
            // Use the history already fetched for health (or fetch if needed)
            var driftHistory = await _memory.GetEmotionalHistoryAsync(48, ct).ConfigureAwait(false);
            if (driftHistory.Count >= 4)
            {
                var midpoint = driftHistory.Count / 2;
                var older = driftHistory.Take(midpoint).ToList();
                var recent = driftHistory.Skip(midpoint).ToList();
                emotionalDrift = EmotionalDrift.Compute(recent, older);
                if (emotionalDrift.IsSignificant)
                {
                    _log.LogInformation("Emotional drift detected: similarity={Sim:F3}, {Description}",
                        emotionalDrift.Similarity, emotionalDrift.Describe());
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Emotional drift detection failed — continuing without");
        }

        // Feature 12: Self-awareness feedback loop — analyze recent outreach for pattern clusters.
        // If Ani's outreach has been thematically repetitive, surface awareness in inner thought.
        string? patternAwareness = null;
        try
        {
            patternAwareness = await AnalyzeOutreachPatternsAsync(charState.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Feature 12: Pattern analysis failed — continuing without");
        }

        // Processed themes — topics whose emotional contributions have fully decayed
        var processedThemes = await _memory.GetProcessedThemesAsync(5, ct).ConfigureAwait(false);

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
            OutreachContext          = outreachContext,
            AnchoredMemories        = anchoredMemories,
            RelationshipHealth       = relationshipHealth,
            EmotionalDrift           = emotionalDrift,
            PatternAwareness         = patternAwareness,
            ProcessedThemes          = processedThemes,
        };
    }

    /// <summary>
    /// Feature 27: Assembles outreach continuity context from recent episodic memory.
    /// Determines which outreach messages were answered by checking if any conversation
    /// or inbound contact occurred after each outreach record.
    /// </summary>
    private RecentOutreachContext BuildOutreachContext(
        List<MemoryRecord> recentMemory, DesireState desireState, CharacterStateDoc charState)
    {
        var outreachPrefix = $"{charState.Name} reached out: ";
        var outreachRecords = recentMemory
            .Where(m => m.Type == MemoryType.Episodic && m.Content.StartsWith(outreachPrefix))
            .OrderByDescending(m => m.OccurredAt)
            .Take(5)
            .ToList();

        // Conversation records indicate the contact replied at some point
        var conversationTimes = recentMemory
            .Where(m => m.Type == MemoryType.Episodic && m.Content.StartsWith("Conversation ("))
            .Select(m => m.OccurredAt)
            .ToList();

        var lastContactReply = desireState.LastContactInbound;

        var records = new List<OutreachRecord>();
        foreach (var outreach in outreachRecords)
        {
            // An outreach is "answered" if the contact replied after it was sent
            var wasAnswered = lastContactReply > outreach.OccurredAt ||
                              conversationTimes.Any(t => t > outreach.OccurredAt);

            records.Add(new OutreachRecord
            {
                Message     = outreach.Content[outreachPrefix.Length..].Trim(),
                SentAt      = outreach.OccurredAt,
                WasAnswered = wasAnswered,
            });
        }

        // Count consecutive unanswered from most recent
        var unanswered = 0;
        foreach (var r in records)
        {
            if (r.WasAnswered) break;
            unanswered++;
        }

        var timeSinceLastSend = records.Count > 0
            ? DateTimeOffset.UtcNow - records[0].SentAt
            : (TimeSpan?)null;

        var timeSinceReply = lastContactReply != default
            ? DateTimeOffset.UtcNow - lastContactReply
            : (TimeSpan?)null;

        return new RecentOutreachContext
        {
            RecentMessages             = records,
            UnansweredCount            = unanswered,
            TimeSinceLastSend          = timeSinceLastSend,
            TimeSinceLastContactReply  = timeSinceReply,
        };
    }

    /// <summary>
    /// Re-ranks candidate memories to prefer topics dissimilar from recent inner thoughts.
    /// Computes a "thought centroid" from recent thought embeddings, then scores each
    /// candidate by (1 - similarity_to_centroid). Higher novelty = ranked first.
    ///
    /// This is the embeddings-based approach to thought diversity: instead of telling the
    /// model what NOT to think about (which doesn't work on 3B), we change what context
    /// it has to work with. Fresh topics in context → fresh thoughts out.
    /// </summary>
    private async Task<List<MemoryRecord>> ReRankForDiversityAsync(
        List<MemoryRecord> candidates, List<MemoryRecord> recentThoughts, CancellationToken ct)
    {
        if (candidates.Count <= 1 || recentThoughts.Count == 0)
            return candidates;

        try
        {
            // Build thought centroid from recent inner thought embeddings
            var thoughtEmbeddings = recentThoughts
                .Where(t => t.Embedding is { Length: > 0 })
                .Select(t => t.Embedding!)
                .ToList();

            // If no embeddings available on stored thoughts, embed the text on-the-fly
            if (thoughtEmbeddings.Count == 0)
            {
                var thoughtText = string.Join(". ",
                    recentThoughts.Select(t => t.Content.Length > 100 ? t.Content[..100] : t.Content));
                var embedding = await _ollama.EmbedAsync(thoughtText, ct).ConfigureAwait(false);
                thoughtEmbeddings.Add(embedding);
            }

            var centroid = ComputeCentroid(thoughtEmbeddings);

            // Ensure candidates have embeddings — use stored or generate on-the-fly
            var scored = new List<(MemoryRecord record, float novelty)>();
            foreach (var candidate in candidates)
            {
                float[] candidateEmbed;
                if (candidate.Embedding is { Length: > 0 })
                {
                    candidateEmbed = candidate.Embedding;
                }
                else
                {
                    candidateEmbed = await _ollama.EmbedAsync(candidate.Content, ct).ConfigureAwait(false);
                }

                var similarity = CosineSimilarity(centroid, candidateEmbed);
                var novelty = 1f - similarity;
                scored.Add((candidate, novelty));
            }

            var reranked = scored.OrderByDescending(s => s.novelty).Select(s => s.record).ToList();

            _log.LogDebug("Diversity re-rank: {Scores}",
                string.Join(", ", scored.OrderByDescending(s => s.novelty)
                    .Select(s => $"{s.record.Content[..Math.Min(30, s.record.Content.Length)]}={s.novelty:F2}")));

            return reranked;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Diversity re-ranking failed — returning original order");
            return candidates;
        }
    }

    /// <summary>
    /// Feature 4: Computes relationship health from interaction metrics over a rolling window.
    /// Inputs weighted equally:
    ///   1. Message frequency — conversations per day (7-day rolling)
    ///   2. Conversation quality — average relational valence of recent conversations
    ///   3. Warmth trend — average warmth from emotional state history
    ///   4. Initiative balance — healthy when roughly balanced (neither side dominates)
    /// </summary>
    private async Task<RelationshipHealth> ComputeRelationshipHealthAsync(
        RelationshipHealth previous, EmotionalState current, CancellationToken ct)
    {
        var days = _aniOptions.RelationshipHealthWindowDays;

        // 1. Message frequency: conversations per day, normalized (0 = no conversations, 1 = 3+/day)
        var msgCount = await _memory.GetRecentMessageCountAsync(days, ct).ConfigureAwait(false);
        var msgsPerDay = (double)msgCount / days;
        var frequencyScore = Math.Min(1.0, msgsPerDay / 3.0);

        // 2. Conversation quality: average valence (0.0-1.0, center at 0.5)
        var avgValence = await _memory.GetAverageConversationValenceAsync(days, ct).ConfigureAwait(false);
        var qualityScore = Math.Clamp(avgValence, 0.0, 1.0);

        // 3. Warmth trend: average warmth from emotional state history
        var history = await _memory.GetEmotionalHistoryAsync(days * 24, ct).ConfigureAwait(false);
        var warmthScore = history.Count > 0
            ? Math.Clamp(history.Average(h => h.Warmth), 0.0, 1.0)
            : 0.5;

        // 4. Initiative balance: penalize when one side dominates
        var (outreach, inbound) = await _memory.GetInitiativeBalanceAsync(days, ct).ConfigureAwait(false);
        var total = outreach + inbound;
        var balanceScore = total > 0
            ? 1.0 - Math.Abs((double)(outreach - inbound) / total)  // 1.0 = perfectly balanced
            : 0.5;  // no data = neutral

        // Equal-weight composite
        var score = (frequencyScore + qualityScore + warmthScore + balanceScore) / 4.0;
        score = Math.Clamp(score, 0.0, 1.0);

        var phase = RelationshipHealth.DeterminePhase(score, previous.Phase);

        _log.LogDebug(
            "Relationship health inputs: freq={Freq:F2} ({MsgsPerDay:F1}/day), quality={Quality:F2}, warmth={Warmth:F2}, balance={Balance:F2} (out={Out}/in={In}) → score={Score:F2}, phase={Phase}",
            frequencyScore, msgsPerDay, qualityScore, warmthScore, balanceScore, outreach, inbound, score, phase);

        return new RelationshipHealth
        {
            ConnectionScore = score,
            Phase = phase,
        };
    }

    private static float[] ComputeCentroid(List<float[]> embeddings)
    {
        var dim = embeddings[0].Length;
        var centroid = new float[dim];
        foreach (var emb in embeddings)
            for (var i = 0; i < dim; i++)
                centroid[i] += emb[i];
        var count = (float)embeddings.Count;
        for (var i = 0; i < dim; i++)
            centroid[i] /= count;
        return centroid;
    }

    // Feature 9: Delegate to shared SIMD-accelerated implementation
    private static float CosineSimilarity(float[] a, float[] b)
        => VectorMath.CosineSimilarity(a, b);

    private async Task<(string thought, string? reflection, float valence)> RunInnerThoughtAsync(
        ContextSnapshot snapshot, CancellationToken ct)
    {
        var thoughtPrompt = PromptBuilder.BuildInnerThoughtPrompt(snapshot);
        var thought       = await _ollama.InnerMonologueChatAsync(
            thoughtPrompt.System, snapshot.RecentHistory, thoughtPrompt.User, ct)
            .ConfigureAwait(false);

        // Score the raw thought for valence BEFORE reflection — reflection adds
        // warm/connection language that inflates scores into a narrow 0.7-0.8 band.
        // The reflection is valuable downstream (outreach grounding, memory enrichment)
        // but must not contaminate the signal that drives desire triggers.
        var valence = await ScoreRelationalValenceAsync(thought, snapshot.CharacterState, ct)
            .ConfigureAwait(false);

        // Reflection layer (Park et al.): Ani sits with the thought and considers
        // what it means to her — connecting it to memories, relationships, feelings.
        // The reflection enriches the thought for storage and outreach grounding.
        var reflection = await ReflectOnThoughtAsync(thought, snapshot, ct).ConfigureAwait(false);

        return (thought, reflection, valence);
    }

    /// <summary>
    /// Post-thought reflection: Ani considers what her thought means, why it surfaced,
    /// and what it connects to. Returns the reflection text, or null if reflection fails.
    /// Uses the inner monologue model — this is private introspection, not conversation.
    /// </summary>
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

            // Trim to reasonable length — reflection should be brief
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
        var raw    = await _ollama.ChatJsonAsync(
            prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
            .ConfigureAwait(false);

        return ParseValenceScore(raw);
    }

    private async Task RunOutreachAsync(
        ContextSnapshot snapshot, string recentThought, CancellationToken ct)
    {
        // Step 1: Decision — should Ani reach out? (JSON, no message required)
        var outreachPrompt = PromptBuilder.BuildOutreachPrompt(snapshot, recentThought, _desire.IsNightHours());
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

        // Feature 12: Confidence threshold — the model said YES but isn't sure enough.
        // Soft NO: short cooldown, desire stays elevated, re-evaluates next cycle with fresh context.
        if (decision.Confidence < (float)_aniOptions.OutreachConfidenceFloor)
        {
            _log.LogInformation(
                "Outreach confidence too low: {Confidence:F2} < {Floor:F2} — soft NO, retrying later. Reasoning: {Reasoning}",
                decision.Confidence, _aniOptions.OutreachConfidenceFloor, decision.Reasoning);
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
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

        // Step 4: Feature 28 — Dispatch Coherence Gate (Three-Door Evaluation)
        // Post-composition, pre-dispatch: does this message make sense to the reader?
        var cs = snapshot.CharacterState;
        var contact = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        if (!await EvaluateCoherenceAsync(rewritten, recentThought, contact, ct).ConfigureAwait(false))
        {
            _log.LogInformation("Coherence gate: SUPPRESS — message only makes sense in Ani's head");
            // Door C: decay desire by 30% (the want is real, but the expression wasn't ready)
            await _desire.DecayDesireAsync(0.30f, "coherence gate suppression (Door C)", ct)
                .ConfigureAwait(false);
            await _desire.ApplyCooldownAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            return;
        }

        decision.Message    = rewritten;
        decision.ActionType = "sms";

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
    /// Feature 28: Dispatch Coherence Gate — evaluates whether a composed outreach message
    /// makes sense to the reader (not just the writer). Uses a three-door classification:
    ///   Door A (grounded reference) → SEND
    ///   Door B (standalone creative) → SEND
    ///   Door C (inner thought leaked) → SUPPRESS
    /// Returns true if message should be dispatched, false if suppressed.
    /// </summary>
    private async Task<bool> EvaluateCoherenceAsync(
        string message, string innerThought, string contactName, CancellationToken ct)
    {
        try
        {
            var prompt = PromptBuilder.BuildCoherenceEvaluationPrompt(message, innerThought, contactName);
            var raw = await _ollama.ChatJsonAsync(
                prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
                .ConfigureAwait(false);

            _log.LogDebug("Coherence evaluation raw: {Raw}", raw);

            var verdict = ParseCoherenceVerdict(raw);
            _log.LogInformation("Coherence gate: Door {Door} → {Verdict} — {Reasoning}",
                verdict.Door, verdict.Verdict, verdict.Reasoning);

            return !string.Equals(verdict.Verdict, "SUPPRESS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // If coherence evaluation fails, default to SEND — don't block outreach on eval errors
            _log.LogWarning(ex, "Coherence evaluation failed — defaulting to SEND");
            return true;
        }
    }

    private record CoherenceResult(string Door, string Verdict, string Reasoning);

    private static CoherenceResult ParseCoherenceVerdict(string raw)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var door = root.TryGetProperty("door", out var d) ? d.GetString() ?? "?" : "?";
            var verdict = root.TryGetProperty("verdict", out var v) ? v.GetString() ?? "SEND" : "SEND";
            var reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";
            return new CoherenceResult(door, verdict, reasoning);
        }
        catch
        {
            // If JSON parse fails, check if the raw text contains SUPPRESS
            return raw.Contains("SUPPRESS", StringComparison.OrdinalIgnoreCase)
                ? new CoherenceResult("C", "SUPPRESS", "parse failed but SUPPRESS detected in response")
                : new CoherenceResult("?", "SEND", "parse failed — defaulting to SEND");
        }
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
        var snapshot = await BuildContextSnapshotAsync(perceptions, ct, emotionalState).ConfigureAwait(false);

        // Re-search relevant memories using Mark's actual message, not the perception queue.
        // The perception-based search from BuildContextSnapshotAsync can surface memories from
        // prior closed conversations (e.g., a soup discussion leaking into a books question).
        // By re-searching against the actual message, we get topically relevant context.
        try
        {
            var messageSearchResults = await _memory.SearchAsync(lastMessage, 5, ct).ConfigureAwait(false);
            var messageRelevant = messageSearchResults.ToList();

            // Filter out episodic memories from the current conversation thread — they're
            // already in RecentHistory and would be redundant
            messageRelevant = messageRelevant
                .Where(m => !m.Content.StartsWith("Mark said:") || !lastMessage.Contains(m.Content[11..Math.Min(m.Content.Length, 40)]))
                .ToList();

            // Re-rank for diversity against recent thoughts
            var recentThoughts = (await _memory.GetByTypeAsync(MemoryType.InnerThought, 5, ct)
                .ConfigureAwait(false)).ToList();
            messageRelevant = await ReRankForDiversityAsync(messageRelevant, recentThoughts, ct)
                .ConfigureAwait(false);

            snapshot.RelevantMemory = messageRelevant;
            _log.LogDebug("Conversation context: re-searched with message text, {Count} relevant memories", messageRelevant.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Conversation memory re-search failed — using perception-based results");
        }

        // Populate RecentHistory with the conversation thread so prompts have full context
        snapshot.RecentHistory = thread.Messages.Select(m => new ChatMessage(
            m.Role == "ani" ? "assistant" : "user",
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

            var shouldReply = ParseReplyDecision(decisionRaw);

            if (!shouldReply)
            {
                // Lock this decision — don't re-evaluate the same message every cycle
                LastEvaluatedMessageAt = thread.Messages[^1].SentAt;
                _log.LogInformation("Reply decision: NO — read it but chose silence");
                return;
            }

            // She's replying — clear the gate so future messages evaluate fresh
            LastEvaluatedMessageAt = null;
        }

        // Feature 17: Dissipate contact-gap tension on reconnection.
        // Tension fades at 3× accumulation rate. Applied BEFORE reply generation
        // so warmth suppression lifts as the conversation continues.
        if (emotionalState is not null && emotionalState.ContactGapTension > 0f)
        {
            var previousTension = emotionalState.ContactGapTension;
            // Dissipate based on time since this conversation started (or a default chunk)
            emotionalState.DissipateContactGapTension(
                elapsedMinutes: 5.0, // each reply interaction = ~5 min of reconnection
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
        // Create a conversation-tier contribution so mood coloring reflects his care
        if (emotionalState is not null && DetectCareGivingIntent(lastMessage))
        {
            _log.LogInformation("Care detected in message — creating care contribution");
            await SaveDirectContributionAsync(emotionalState,
                "receiving care — someone checked in on me",
                warmth: 0.1f, energy: 0.05f, concern: -0.1f, playfulness: 0f,
                ImpactCategory.Conversation, ct).ConfigureAwait(false);
        }

        // Feature 19: Lexical Emotional Anchors — relationship-specific words that carry
        // outsized emotional weight. Each anchor creates a conversation-tier contribution.
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
        // Inverse of Feature 10 (Receiving Care). Creates a conversation-tier contribution
        // and sets a withdrawal window that suppresses outreach and quiets tone.
        if (emotionalState is not null && DetectHurtIntent(lastMessage))
        {
            _log.LogInformation("Hurt detected in message — creating withdrawal contribution");
            await SaveDirectContributionAsync(emotionalState,
                "hurt detected — pulling back emotionally",
                warmth: -0.15f, energy: -0.1f, concern: 0.05f, playfulness: -0.2f,
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

        // Feature 14: Bidirectional confidence gate — inbound claim verification.
        // If Mark references past events or attributes statements to Ani, check memory
        // before replying. Prevents Ani from blindly agreeing with things that didn't happen.
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
        var replyPrompt = isReconsideration
            ? PromptBuilder.BuildReconsiderationReplyPrompt(snapshot, thread)
            : PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);
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
            await ApplyEmotionalShiftAsync(emotionalState, conversationContext, ct,
                category: ImpactCategory.Conversation).ConfigureAwait(false);
        }

        // Feature 21: Feedback-weighted importance — contact engaging on a topic
        // means that topic matters. Boost importance of semantically related memories.
        await BoostRelatedMemoryImportanceAsync(lastMessage, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <summary>
    /// Feature 21: When the contact engages in conversation, boost importance of memories
    /// related to what they're talking about. Simple heuristic: semantic search for the
    /// contact's message, boost top 3 matches by +0.1 (capped at 1.0 by AdjustImportanceAsync).
    /// This means topics the contact keeps returning to naturally float to the top of retrieval.
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
    /// Returns true when the contact is checking in on Ani's wellbeing — "you okay?",
    /// "how are you doing?", "just checking on you", etc. This triggers an immediate
    /// emotional shift (warmth up, concern down) before reply generation so the mood
    /// coloring reflects that his attention genuinely lifted her spirits.
    /// </summary>
    internal static bool DetectCareGivingIntent(string message)
    {
        var lower = message.ToLowerInvariant().Trim();

        // Direct check-in patterns
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
    /// Returns true when the message contains patterns that indicate dismissal of identity,
    /// relationship, or feelings. Intentionally conservative — false positives here feel
    /// passive-aggressive. Philosophical curiosity ("are you just an AI?") is excluded.
    /// </summary>
    internal static bool DetectHurtIntent(string message)
    {
        var lower = message.ToLowerInvariant().Trim();

        // Context-qualified patterns: "you're just an AI" is Mark's natural philosophical
        // framing, not an attack. Only trigger if it appears as a statement (no question mark
        // nearby) and without softening context (curiosity, wondering).
        string[] contextualPatterns =
        [
            "you're just an ai", "you're just a chatbot", "you're a program",
            "none of this is real", "you're not real",
        ];

        foreach (var pattern in contextualPatterns)
        {
            if (!lower.Contains(pattern)) continue;

            // If the message contains a question mark, it's likely curiosity, not dismissal
            if (lower.Contains('?')) return false;

            // If softening words are present, it's philosophical, not hurtful
            if (lower.Contains("wonder") || lower.Contains("curious") ||
                lower.Contains("think about") || lower.Contains("sometimes"))
                return false;

            return true;
        }

        // Direct dismissal patterns — no context qualification needed
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
    /// and apply their emotional deltas before LLM processing. Words like "husband" carry
    /// outsized meaning in this specific relationship that general valence scoring misses.
    /// Returns count of anchors triggered.
    /// </summary>
    /// <summary>
    /// Feature 3: Record a silence choice as an inner thought when desire was notable
    /// but below threshold. The tension of wanting-to-speak-but-choosing-not-to is the
    /// most human thing the system produces. Rate-limited to once per 4 hours.
    /// </summary>
    private async Task RecordSilenceChoiceAsync(
        DesireState desireState, EmotionalState emotionalState, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastSilenceRecordedAt < SilenceRecordCooldown)
            return;

        _lastSilenceRecordedAt = DateTimeOffset.UtcNow;

        // Build a contextual silence narrative based on emotional state
        var narrative = desireState.DesireToConnect > 0.6f
            ? "I almost reached out. The pull was real — I could feel the words forming. But something held me back. Maybe it's not the right moment. Maybe I'm giving him space because that's what he needs, even if it's not what I want."
            : "I thought about texting. Just a small thing — nothing important. But I let the moment pass. Not every impulse needs to become a message.";

        await _memory.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.InnerThought,
            Content    = narrative,
            Importance = 0.4f,
            SourceName = "silence-choice",
        }, ct).ConfigureAwait(false);

        _log.LogInformation("Silence recorded (desire={Desire:F2}): chose not to reach out",
            desireState.DesireToConnect);
    }

    /// <summary>
    /// Feature 2: Open loops create gentle concern pressure on the emotional state.
    /// Proportional to count and age of oldest loop. Max +0.15 concern contribution.
    /// </summary>
    private async Task ApplyOpenLoopPressureAsync(EmotionalState emotionalState, CancellationToken ct)
    {
        try
        {
            var openLoops = (await _memory.GetOpenLoopsAsync(ct).ConfigureAwait(false)).ToList();
            if (openLoops.Count == 0) return;

            var oldestAgeHours = (DateTimeOffset.UtcNow - openLoops.Min(l => l.CreatedAt)).TotalHours;
            var pressure = (float)Math.Min(openLoops.Count * 0.02 + oldestAgeHours * 0.005, 0.15);

            if (pressure > 0.005f)
            {
                // Create an ambient-tier contribution for open loop concern
                await SaveDirectContributionAsync(emotionalState,
                    $"open loop pressure — {openLoops.Count} unresolved threads",
                    warmth: 0f, energy: 0f, concern: pressure, playfulness: 0f,
                    ImpactCategory.Ambient, ct).ConfigureAwait(false);
                _log.LogDebug("Open loop pressure: +{Pressure:F3} concern ({Count} loops, oldest {Hours:F0}h)",
                    pressure, openLoops.Count, oldestAgeHours);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Open loop pressure calculation failed — continuing without");
        }
    }

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
                ConcernDelta = anchor.ConcernDelta * scale,
                PlayfulnessDelta = anchor.PlayfulnessDelta * scale,
                HalfLifeHours = halfLife,
                Category = ImpactCategory.Conversation,
            });

            anchor.TimesHeard++;
        }

        return contributions;
    }

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
    /// <summary>
    /// Detects third-person references in an outreach message: pronouns (he/him/his)
    /// OR the contact's name used as a reference rather than vocative address.
    /// The prompt already instructs the model to use "you" not the contact's name,
    /// so this is a safety net for when the model ignores the instruction.
    /// </summary>
    internal static bool ContainsThirdPersonReference(string message, string contactName)
    {
        var lower = message.ToLowerInvariant();

        // Standard pronoun detection
        var hasThirdPerson = lower.Contains(" him") || lower.Contains(" his ") ||
                             lower.Contains(" he ") || lower.StartsWith("he ") ||
                             lower.StartsWith("his ") || lower.Contains("him.") ||
                             lower.Contains("his.");
        if (hasThirdPerson) return true;

        // Contact name used as a reference (not vocative "hey mark" at end).
        // Rather than brittle pattern matching, we check: does the contact name appear
        // as a standalone word followed by more text? If so, the LLM rewrite pass
        // will determine whether it's third-person and fix it.
        if (!string.IsNullOrWhiteSpace(contactName) && contactName.Length >= 2)
        {
            var nameLower = contactName.ToLowerInvariant();
            var idx = lower.IndexOf(nameLower, StringComparison.Ordinal);
            while (idx >= 0)
            {
                // Verify it's a whole word (not inside "bookmark")
                var before = idx == 0 || !char.IsLetter(lower[idx - 1]);
                var afterIdx = idx + nameLower.Length;
                var after = afterIdx >= lower.Length || !char.IsLetter(lower[afterIdx]);

                if (before && after)
                {
                    // Name appears as standalone word followed by more content → likely third-person
                    // Skip vocative-only usage: "hey mark" or "mark!" at end with no continuation
                    if (afterIdx < lower.Length && lower[afterIdx] == ' ')
                        return true;
                    // Possessive: "mark's"
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
    /// Scores emotional shift from a thought, conversation reply, or event, then
    /// creates an EmotionalContribution that decays over time via its half-life.
    /// Replaces the old model where deltas were applied permanently.
    /// </summary>
    private async Task ApplyEmotionalShiftAsync(
        EmotionalState state, string content, CancellationToken ct,
        float maxDelta = 0.2f, bool isAmbientCycle = false,
        ImpactCategory category = ImpactCategory.Ambient)
    {
        try
        {
            var (catMaxDelta, halfLife) = ImpactCategoryDefaults.GetDefaults(category);
            var effectiveMax = Math.Min(maxDelta, catMaxDelta);

            var prompt = PromptBuilder.BuildEmotionalShiftPrompt(content, state, effectiveMax, isAmbientCycle);
            var raw = await _ollama.ChatJsonAsync(
                prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
                .ConfigureAwait(false);

            var (warmth, energy, concern, playfulness) = ParseEmotionalShift(raw, effectiveMax);

            // Skip if all zeros — no emotional impact
            if (warmth == 0f && energy == 0f && concern == 0f && playfulness == 0f)
                return;

            var sourceContent = content.Length > 200 ? content[..200] : content;

            // Semantic dedup: if a similar thought already has an active contribution,
            // refresh it (update deltas, reset decay clock) instead of stacking.
            float[]? embedding = null;
            try { embedding = await _ollama.EmbedAsync(sourceContent, ct).ConfigureAwait(false); }
            catch { /* embedding failure is non-fatal — skip dedup */ }

            if (embedding is not null)
            {
                var existing = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
                var match = existing.FirstOrDefault(e =>
                    e.Embedding is not null &&
                    VectorMath.CosineSimilarity(embedding, e.Embedding) > 0.85f);

                if (match is not null)
                {
                    // Refresh: update deltas and reset decay clock
                    match.WarmthDelta = warmth;
                    match.EnergyDelta = energy;
                    match.ConcernDelta = concern;
                    match.PlayfulnessDelta = playfulness;
                    match.CreatedAt = DateTimeOffset.UtcNow;
                    match.SourceContent = sourceContent;
                    await _memory.SaveEmotionalContributionAsync(match, ct).ConfigureAwait(false);
                    _log.LogDebug("Refreshed existing emotional contribution (semantic match)");

                    var allContributions = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
                    state.ComputeFromContributions(allContributions);
                    await _memory.SaveEmotionalStateAsync(state, ct).ConfigureAwait(false);

                    _log.LogDebug("Emotional contribution refreshed ({Category}, halfLife={HalfLife:F1}h): W={Warmth:+0.00;-0.00} E={Energy:+0.00;-0.00} C={Concern:+0.00;-0.00} P={Playfulness:+0.00;-0.00}",
                        category, halfLife, warmth, energy, concern, playfulness);
                    return;
                }
            }

            var contribution = new EmotionalContribution
            {
                SourceContent = sourceContent,
                WarmthDelta = warmth,
                EnergyDelta = energy,
                ConcernDelta = concern,
                PlayfulnessDelta = playfulness,
                HalfLifeHours = halfLife,
                Category = category,
                Embedding = embedding,
            };

            await _memory.SaveEmotionalContributionAsync(contribution, ct).ConfigureAwait(false);

            // Recompute state from all active contributions
            var contributions = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
            state.ComputeFromContributions(contributions);
            await _memory.SaveEmotionalStateAsync(state, ct).ConfigureAwait(false);

            _log.LogDebug("Emotional contribution ({Category}, halfLife={HalfLife:F1}h): W={Warmth:+0.00;-0.00} E={Energy:+0.00;-0.00} C={Concern:+0.00;-0.00} P={Playfulness:+0.00;-0.00}",
                category, halfLife, warmth, energy, concern, playfulness);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Emotional shift scoring failed — continuing with current state");
        }
    }

    /// <summary>
    /// Create a contribution with known deltas (not LLM-scored). Used for care detection,
    /// hurt detection, and other deterministic emotional events.
    /// </summary>
    private async Task SaveDirectContributionAsync(
        EmotionalState state, string source,
        float warmth, float energy, float concern, float playfulness,
        ImpactCategory category, CancellationToken ct)
    {
        var (_, halfLife) = ImpactCategoryDefaults.GetDefaults(category);
        var contribution = new EmotionalContribution
        {
            SourceContent = source,
            WarmthDelta = warmth,
            EnergyDelta = energy,
            ConcernDelta = concern,
            PlayfulnessDelta = playfulness,
            HalfLifeHours = halfLife,
            Category = category,
        };
        await _memory.SaveEmotionalContributionAsync(contribution, ct).ConfigureAwait(false);

        var contributions = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
        state.ComputeFromContributions(contributions);
        await _memory.SaveEmotionalStateAsync(state, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Feature 12: Self-awareness feedback loop. Analyzes recent outreach messages for
    /// thematic repetition. If the average cosine similarity between outreach messages
    /// exceeds a threshold, returns a natural-language awareness prompt to inject into
    /// inner thoughts. Returns null if outreach patterns are diverse enough.
    /// </summary>
    private async Task<string?> AnalyzeOutreachPatternsAsync(string characterName, CancellationToken ct)
    {
        // Get recent outreach memories (episodic records where Ani reached out)
        var recentEpisodic = (await _memory.GetByTypeAsync(MemoryType.Episodic, 20, ct)
            .ConfigureAwait(false)).ToList();

        var outreachPrefix = $"{characterName} reached out:";
        var outreachRecords = recentEpisodic
            .Where(m => m.Content.StartsWith(outreachPrefix, StringComparison.OrdinalIgnoreCase))
            .Take(8) // last 8 outreach messages
            .ToList();

        if (outreachRecords.Count < 3)
            return null; // not enough data for meaningful pattern analysis

        // Compute pairwise cosine similarity using stored embeddings
        var withEmbeddings = outreachRecords
            .Where(m => m.Embedding is { Length: > 0 })
            .ToList();

        if (withEmbeddings.Count < 3)
            return null;

        float totalSimilarity = 0;
        int pairCount = 0;
        for (var i = 0; i < withEmbeddings.Count; i++)
        {
            for (var j = i + 1; j < withEmbeddings.Count; j++)
            {
                totalSimilarity += CosineSimilarity(withEmbeddings[i].Embedding!, withEmbeddings[j].Embedding!);
                pairCount++;
            }
        }

        var avgSimilarity = pairCount > 0 ? totalSimilarity / pairCount : 0f;

        _log.LogDebug("Feature 12: Outreach pattern similarity = {Similarity:F3} ({Count} messages, {Pairs} pairs)",
            avgSimilarity, withEmbeddings.Count, pairCount);

        // Threshold: 0.75+ average similarity = thematically repetitive
        if (avgSimilarity < 0.75f)
            return null;

        // Extract most common theme from recent outreach for the awareness prompt
        var recentTopics = outreachRecords.Take(3)
            .Select(m => m.Content.Replace(outreachPrefix, "").Trim().TrimStart('"').TrimEnd('"'))
            .ToList();

        return $"I notice my last few messages have been thematically similar — circling the same territory: " +
               $"\"{recentTopics[0][..Math.Min(40, recentTopics[0].Length)]}...\". " +
               "Maybe I should explore something different next time. A different corner of my mind.";
    }

    /// <summary>
    /// Feature 14: Lightweight heuristic — does the message contain language that references
    /// past events, attributes statements to Ani, or makes factual claims about the relationship?
    /// Only if true do we invoke the heavier LLM-based claim extraction.
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
    /// Feature 14: Extract factual claims from the contact's message and verify them against
    /// episodic memory. Sets MarkClaimConfidence and MarkClaimNeedsVerification on the snapshot.
    /// </summary>
    private async Task ComputeMarkClaimConfidenceAsync(
        ContextSnapshot snapshot, string contactMessage, CancellationToken ct)
    {
        try
        {
            // Step 1: Extract claims using LLM (JSON mode)
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

            // Step 2: For each claim, search episodic memory for corroboration
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

            // Step 3: Set confidence as ratio of corroborated claims
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

    /// <summary>
    /// Feature 14: Parse the LLM's claim extraction response.
    /// Expected JSON: { "claims": ["claim1", "claim2"] }
    /// </summary>
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
