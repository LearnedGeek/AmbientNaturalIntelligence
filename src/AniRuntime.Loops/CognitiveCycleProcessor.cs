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
/// This is a thin orchestrator — each phase delegates to a focused class:
///   EmotionalProcessor, ContextBuilder, ConversationReplyPhase, OutreachPhase.
/// </summary>
public class CognitiveCycleProcessor
{
    private readonly IMemoryService                  _memory;
    private readonly IOllamaClient                   _ollama;
    private readonly DesireEngine                    _desire;
    private readonly IEnumerable<IPerceptionSource>  _sources;
    private readonly AdminCommandHandler             _adminCommands;
    private readonly IEmergenceObserver              _emergence;
    private readonly IConversationService            _conversations;
    private readonly EmotionalProcessor              _emotional;
    private readonly ContextBuilder                  _contextBuilder;
    private readonly ConversationReplyPhase          _conversationReply;
    private readonly OutreachPhase                   _outreach;
    private readonly AniOptions                      _aniOptions;
    private readonly ILogger<CognitiveCycleProcessor> _log;

    private DateTimeOffset _lastCycleAt = DateTimeOffset.UtcNow;

    // Dedup cache: prevents saving the same perception (e.g. "probably at the gym")
    // every cycle. Key = summary text, Value = when it was last persisted.
    private readonly Dictionary<string, DateTimeOffset> _recentPerceptions = new();
    private static readonly TimeSpan PerceptionDedupeWindow = TimeSpan.FromHours(4);

    /// <summary>
    /// Tracks the last contact message we evaluated a reply decision for.
    /// Delegated to ConversationReplyPhase but exposed here for heartbeat service access.
    /// </summary>
    public DateTimeOffset? LastEvaluatedMessageAt => _conversationReply.LastEvaluatedMessageAt;

    public CognitiveCycleProcessor(
        IMemoryService                 memory,
        IOllamaClient                  ollama,
        DesireEngine                   desire,
        AniActionDispatcher            dispatcher,
        IConversationService           conversations,
        IEnumerable<IPerceptionSource> sources,
        AdminCommandHandler            adminCommands,
        IEmergenceObserver             emergence,
        EmotionalProcessor             emotional,
        ContextBuilder                 contextBuilder,
        ConversationReplyPhase         conversationReply,
        OutreachPhase                  outreach,
        IOptions<AniOptions>           aniOptions,
        ILogger<CognitiveCycleProcessor> log)
    {
        _memory            = memory;
        _ollama            = ollama;
        _desire            = desire;
        _conversations     = conversations;
        _sources           = sources;
        _adminCommands     = adminCommands;
        _emergence         = emergence;
        _emotional         = emotional;
        _contextBuilder    = contextBuilder;
        _conversationReply = conversationReply;
        _outreach          = outreach;
        _aniOptions        = aniOptions.Value;
        _log               = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogDebug("Cognitive cycle starting");

        // ── Emergence observation tracking (populated throughout, published in finally) ──
        string? obsThought = null, obsReflection = null, obsRegister = null;
        string? obsOutcome = null, obsOutreachMsg = null, obsCoherenceDoor = null;
        string? obsContactMsg = null, obsReplyMsg = null;
        float obsValence = 0, obsDesire = 0, obsSeverity = 0;
        float obsWDelta = 0, obsEDelta = 0, obsWoDelta = 0, obsPDelta = 0;
        bool obsDesireCrossed = false, obsConversation = false, obsSilence = false;
        EmotionalState? obsEmotional = null;
        List<string> obsPerceptions = new();

        try
        {
        // Phase 0: Emotional state — compute from active contributions.
        // Each contribution decays independently via its own half-life.
        // The emotional state is baselines + sum of all decayed contributions.
        var emotionalState = await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var activeContributions = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
        emotionalState.ComputeFromContributions(activeContributions);
        obsEmotional = emotionalState;

        // Periodic cleanup of fully-decayed contributions (> 24h old)
        await _memory.CleanupDecayedContributionsAsync(ct).ConfigureAwait(false);

        // Feature 2: Open loops as emotional weight — unresolved threads create gentle
        // worry pressure. Proportional to count and age of oldest loop.
        await _emotional.ApplyOpenLoopPressureAsync(emotionalState, ct).ConfigureAwait(false);

        // Feature 17: Contact-gap tension — relational ache builds during prolonged absence.
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
        obsPerceptions = perceptions.Select(p => p.Summary).ToList();

        // Persist notable perceptions so they accumulate embeddings and feed future
        // semantic search. Without this, perceptions are ephemeral — gone after one cycle.
        await PersistNotablePerceptionsAsync(perceptions, ct).ConfigureAwait(false);

        // Load character state early — needed for dynamic name references in logs and memory
        var charState = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);

        // Phase 2: Check for active conversation — if contact texted, route to reply mode
        var activeThread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
        var hasUnreadFromContact = activeThread?.Messages.Count > 0 &&
                                   activeThread.Messages[^1].Role == Roles.Mark;

        if (hasUnreadFromContact)
        {
            // Record inbound contact — feeds desire drift timing
            await _desire.RecordInboundContactAsync(ct).ConfigureAwait(false);

            var lastMsg = activeThread!.Messages[^1];
            obsContactMsg = lastMsg.Content;
            obsConversation = true;

            // Admin commands bypass conversation entirely — handle and exit
            if (AdminCommandHandler.IsAdminCommand(lastMsg.Content))
            {
                _log.LogInformation("Admin command detected: {Content}", lastMsg.Content);

                // Close the thread FIRST so the command doesn't replay if the reply fails
                await _conversations.CloseThreadAsync(activeThread.Id, ct).ConfigureAwait(false);

                await _adminCommands.HandleAsync(lastMsg.Content, ct).ConfigureAwait(false);
                return;
            }

            // If we already evaluated this exact message and decided NO, don't re-ask.
            if (LastEvaluatedMessageAt.HasValue && lastMsg.SentAt == LastEvaluatedMessageAt.Value)
            {
                _log.LogDebug("Already evaluated reply for message at {SentAt} — skipping to ambient mode",
                    lastMsg.SentAt);
            }
            else
            {
                _log.LogInformation("Conversation mode — {Contact}'s last message: {Message}",
                    charState.PrimaryContactName, lastMsg.Content);
                await _conversationReply.RunConversationReplyAsync(activeThread, perceptions, ct, emotionalState).ConfigureAwait(false);
                return;
            }
        }

        // Phase 3: Reactive sharing — high-relevance RSS items shared directly
        if (await _outreach.TryReactiveShareAsync(perceptions, charState, ct).ConfigureAwait(false))
        {
            obsOutcome = "reactive-share";
            return;
        }

        // Phase 4: Context snapshot — built once, shared across all ambient phases
        var snapshot = await _contextBuilder.BuildContextSnapshotAsync(perceptions, ct, emotionalState).ConfigureAwait(false);

        // Phase 4: Inner thought + reflection
        var (thought, reflection, valence) = await RunInnerThoughtAsync(snapshot, ct).ConfigureAwait(false);
        obsThought = thought;
        obsReflection = reflection;
        obsValence = valence;

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

        // Phase 4b: Emotional shift from inner thought
        var preShiftW = emotionalState.Warmth;
        var preShiftE = emotionalState.Energy;
        var preShiftWo = emotionalState.Worry;
        var preShiftP = emotionalState.Playfulness;

        await _emotional.ApplyEmotionalShiftAsync(emotionalState, thought, ct,
            isAmbientCycle: true, category: ImpactCategory.Ambient).ConfigureAwait(false);

        // Re-read emotional state after shift for emergence observation deltas
        var postShift = await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var postContributions = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
        postShift.ComputeFromContributions(postContributions);
        obsWDelta = postShift.Warmth - preShiftW;
        obsEDelta = postShift.Energy - preShiftE;
        obsWoDelta = postShift.Worry - preShiftWo;
        obsPDelta = postShift.Playfulness - preShiftP;

        // Phase 5: Desire update
        await _desire.ApplyDriftAsync(ct).ConfigureAwait(false);

        if (valence > (float)_aniOptions.ValenceTriggerThreshold)
            await _desire.AddTriggerAsync(
                TriggerType.SpontaneousThought, valence,
                $"thought: {thought[..Math.Min(60, thought.Length)]}", ct).ConfigureAwait(false);

        // Capture desire state for emergence observation
        var desireAfter = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        obsDesire = desireAfter.DesireToConnect;

        // Phase 6: Outreach — only if desire crosses threshold
        if (activeThread is not null)
        {
            if (hasUnreadFromContact && LastEvaluatedMessageAt.HasValue &&
                await _desire.ShouldReachOutAsync(ct).ConfigureAwait(false))
            {
                _log.LogInformation("Desire built after choosing silence — reconsidering reply");
                await _conversationReply.RunConversationReplyAsync(activeThread, perceptions, ct, emotionalState,
                    isReconsideration: true).ConfigureAwait(false);
            }
            else
            {
                _log.LogDebug("Active conversation — suppressing ambient outreach");
                obsOutcome = "suppress-conversation";
            }
            return;
        }

        // Feature 18: Suppress outreach during emotional withdrawal
        if (_conversationReply.IsWithdrawn)
        {
            _log.LogInformation("Outreach suppressed: withdrawal active");
            obsOutcome = "withdrawn";
            return;
        }

        if (!await _desire.ShouldReachOutAsync(ct).ConfigureAwait(false))
        {
            _log.LogDebug("Desire below threshold — no outreach this cycle");
            obsOutcome = "no-desire";

            // Feature 3: Silence as active system
            var desireState = await _desire.GetStateAsync(ct).ConfigureAwait(false);
            if (desireState.DesireToConnect > 0.3f)
            {
                obsSilence = true;
                await _outreach.RecordSilenceChoiceAsync(desireState, emotionalState, ct).ConfigureAwait(false);
            }

            return;
        }

        obsDesireCrossed = true;

        // Feature 27: Hard runtime gates — outreach continuity checks
        var outreachCtx = snapshot.OutreachContext;
        if (outreachCtx is not null)
        {
            if (outreachCtx.UnansweredCount >= _aniOptions.MaxUnansweredBeforeSilence)
            {
                _log.LogInformation(
                    "Outreach suppressed: {Count} unanswered messages (limit={Limit}) — waiting for reply",
                    outreachCtx.UnansweredCount, _aniOptions.MaxUnansweredBeforeSilence);
                obsOutcome = "suppress-gates";
                return;
            }

            if (outreachCtx.TimeSinceLastSend.HasValue &&
                outreachCtx.TimeSinceLastSend.Value.TotalMinutes < _aniOptions.MinSendGapMinutes)
            {
                _log.LogInformation(
                    "Outreach suppressed: only {Minutes:F0} min since last send (minimum={Gap} min)",
                    outreachCtx.TimeSinceLastSend.Value.TotalMinutes, _aniOptions.MinSendGapMinutes);
                obsOutcome = "suppress-gates";
                return;
            }
        }

        await _outreach.RunOutreachAsync(snapshot, thought, ct).ConfigureAwait(false);
        obsOutcome = "send";

        } // end try
        finally
        {
            _lastCycleAt = DateTimeOffset.UtcNow;

            // Publish observation to emergence layer — must never crash the cognitive cycle
            try
            {
                var observation = new CycleObservation
                {
                    Timestamp             = DateTimeOffset.UtcNow,
                    InnerThought          = obsThought,
                    Reflection            = obsReflection,
                    RelationalValence     = obsValence,
                    Warmth                = obsEmotional?.Warmth ?? 0,
                    Energy                = obsEmotional?.Energy ?? 0,
                    Worry                 = obsEmotional?.Worry ?? 0,
                    Playfulness           = obsEmotional?.Playfulness ?? 0,
                    WarmthDelta           = obsWDelta,
                    EnergyDelta           = obsEDelta,
                    WorryDelta            = obsWoDelta,
                    PlayfulnessDelta      = obsPDelta,
                    Severity              = obsSeverity,
                    Register              = obsRegister,
                    DesireToConnect        = obsDesire,
                    DesireThresholdCrossed = obsDesireCrossed,
                    OutreachOutcome        = obsOutcome,
                    OutreachMessage        = obsOutreachMsg,
                    CoherenceGateDoor      = obsCoherenceDoor,
                    WasConversationCycle   = obsConversation,
                    ContactMessage         = obsContactMsg,
                    ReplyMessage           = obsReplyMsg,
                    ChoseSilence           = obsSilence,
                    PerceptionSummaries    = obsPerceptions,
                };
                await _emergence.OnCycleCompleteAsync(observation, default).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Emergence observer failed — cycle unaffected");
            }
        }
    }

    // ── Private phases kept in orchestrator ──────────────────────────────────

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
                _log.LogWarning(ex, "Perception source '{Source}' failed — skipping", source.SourceName);
            }
        }

        return events;
    }

    /// <summary>
    /// Saves notable perceptions as memory records so they get embedded and become
    /// findable via semantic search in future cycles.
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
            if (p.ContactRelevance < 0.25f || p.SourceName == "time")
                continue;

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

    private async Task<(string thought, string? reflection, float valence)> RunInnerThoughtAsync(
        ContextSnapshot snapshot, CancellationToken ct)
    {
        var thoughtPrompt = PromptBuilder.BuildInnerThoughtPrompt(snapshot);
        var thought       = await _ollama.InnerMonologueChatAsync(
            thoughtPrompt.System, snapshot.RecentHistory, thoughtPrompt.User, ct)
            .ConfigureAwait(false);

        // Score the raw thought for valence BEFORE reflection
        var valence = await ScoreRelationalValenceAsync(thought, snapshot.CharacterState, ct)
            .ConfigureAwait(false);

        // Reflection layer (Park et al.)
        var reflection = await ReflectOnThoughtAsync(thought, snapshot, ct).ConfigureAwait(false);

        return (thought, reflection, valence);
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
        var raw    = await _ollama.ChatJsonAsync(
            prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
            .ConfigureAwait(false);

        return ParseValenceScore(raw);
    }

    // ── Parsing helpers ─────────────────────────────────────────────────────

    private static float ParseValenceScore(string raw)
    {
        try
        {
            var doc   = System.Text.Json.JsonDocument.Parse(raw.Trim());
            var score = doc.RootElement.GetProperty("score").GetDouble();
            return (float)Math.Clamp(score, 0.0, 1.0);
        }
        catch
        {
            return 0.3f;
        }
    }

    // ── Static forwarding methods for backward compatibility ────────────────
    // Tests reference CognitiveCycleProcessor.StaticMethod() — these delegate
    // to the phase classes where the logic now lives.

    internal static bool DetectCareGivingIntent(string message)
        => ConversationReplyPhase.DetectCareGivingIntent(message);

    internal static bool DetectHurtIntent(string message)
        => ConversationReplyPhase.DetectHurtIntent(message);

    internal static List<EmotionalContribution> BuildLexicalAnchorContributions(
        string message, CharacterStateDoc charState)
        => ConversationReplyPhase.BuildLexicalAnchorContributions(message, charState);

    internal static bool EndsWithDirectQuestion(string message)
        => ConversationReplyPhase.EndsWithDirectQuestion(message);

    internal static bool ContainsMemoryReferencingLanguage(string message)
        => ConversationReplyPhase.ContainsMemoryReferencingLanguage(message);

    internal static bool ContainsThirdPersonReference(string message, string contactName)
        => OutreachPhase.ContainsThirdPersonReference(message, contactName);
}
