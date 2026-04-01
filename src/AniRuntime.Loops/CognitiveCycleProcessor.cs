using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using LearnedGeek.ML.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Ani's full cognitive cycle, executed once per scheduled wake.
///
/// Phase sequence:
///   0. Emotional state — compute from active contributions
///   1. Perception      — poll all enabled sources since last cycle
///   2. Conversation    — if contact texted, route to reply mode
///   3. Reactive share  — high-relevance RSS items shared directly
///   4. Inner thought   — private LLM call; score contact valence; persist
///   5. Desire update   — apply temporal drift and trigger weights
///   6. Outreach        — conditional on desire threshold; dispatch or cooldown
///
/// This is a thin orchestrator — each phase delegates to a focused class:
///   PerceptionPhase, InnerThoughtPhase, EmotionalProcessor,
///   ContextBuilder, ConversationReplyPhase, OutreachPhase.
/// </summary>
public class CognitiveCycleProcessor
{
    private readonly IStateStore                       _state;
    private readonly IMemoryPersistence                _persist;
    private readonly IMemoryAnalytics                  _analytics;
    private readonly IMemoryMaintenance                _maintenance;
    private readonly DesireEngine                      _desire;
    private readonly IConversationService              _conversations;
    private readonly AdminCommandHandler               _adminCommands;
    private readonly IEmergenceObserver                _emergence;
    private readonly EmotionalProcessor                _emotional;
    private readonly ContextBuilder                    _contextBuilder;
    private readonly PerceptionPhase                   _perception;
    private readonly InnerThoughtPhase                 _innerThought;
    private readonly ConversationReplyPhase            _conversationReply;
    private readonly OutreachPhase                     _outreach;
    private readonly ReflectionPhase                   _reflection;
    private readonly IConversationGateState            _gateState;
    private readonly WorldSeedService                  _worldSeed;
    private readonly ITextClassificationService?       _mlClassifier;
    private readonly AniOptions                        _aniOptions;
    private readonly ILogger<CognitiveCycleProcessor>  _log;
    private int _cycleCount;
    private string? _lastAssociativeAnchor;

    public CognitiveCycleProcessor(
        IStateStore                    state,
        IMemoryPersistence             persist,
        IMemoryAnalytics               analytics,
        IMemoryMaintenance             maintenance,
        DesireEngine                   desire,
        IConversationService           conversations,
        AdminCommandHandler            adminCommands,
        IEmergenceObserver             emergence,
        EmotionalProcessor             emotional,
        ContextBuilder                 contextBuilder,
        PerceptionPhase                perception,
        InnerThoughtPhase              innerThought,
        ConversationReplyPhase         conversationReply,
        OutreachPhase                  outreach,
        ReflectionPhase                reflection,
        IConversationGateState         gateState,
        WorldSeedService               worldSeed,
        IOptions<AniOptions>           aniOptions,
        ILogger<CognitiveCycleProcessor> log,
        ITextClassificationService?    mlClassifier = null)
    {
        _state             = state;
        _persist           = persist;
        _analytics         = analytics;
        _maintenance       = maintenance;
        _desire            = desire;
        _conversations     = conversations;
        _adminCommands     = adminCommands;
        _emergence         = emergence;
        _emotional         = emotional;
        _contextBuilder    = contextBuilder;
        _perception        = perception;
        _innerThought      = innerThought;
        _conversationReply = conversationReply;
        _outreach          = outreach;
        _reflection        = reflection;
        _gateState         = gateState;
        _worldSeed         = worldSeed;
        _mlClassifier      = mlClassifier;
        _aniOptions        = aniOptions.Value;
        _log               = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogDebug("Cognitive cycle starting");

        // CS3: Emergence observation builder — replaces 17 scattered local variables.
        // Each phase sets properties on the builder; Build() produces the immutable snapshot.
        var obs = new CycleObservationBuilder();

        try
        {
        // Phase 0: Emotional state — compute from active contributions.
        var emotionalState = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var activeContributions = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);
        emotionalState.ComputeFromContributions(activeContributions);
        obs.EmotionalState = emotionalState;

        // Periodic cleanup of fully-decayed contributions (> 24h old)
        await _maintenance.CleanupDecayedContributionsAsync(ct).ConfigureAwait(false);

        // Feature 2: Open loops as emotional weight
        await _emotional.ApplyOpenLoopPressureAsync(emotionalState, ct).ConfigureAwait(false);

        // Feature 17: Contact-gap tension
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

        await _persist.SaveEmotionalStateAsync(emotionalState, ct).ConfigureAwait(false);

        // Phase 1: Perception — delegated to PerceptionPhase
        var perceptions = await _perception.PollAsync(ct).ConfigureAwait(false);
        obs.PerceptionSummaries = perceptions.Select(p => p.Summary).ToList();
        await _perception.PersistNotableAsync(perceptions, ct).ConfigureAwait(false);

        // Load character state early — needed for dynamic name references
        var charState = await _state.GetCharacterStateAsync(ct).ConfigureAwait(false);

        // Phase 2: Check for active conversation — if contact texted, route to reply mode
        var activeThread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
        var hasUnreadFromContact = activeThread?.Messages.Count > 0 &&
                                   activeThread.Messages[^1].Role == Roles.Mark;

        if (hasUnreadFromContact)
        {
            await _desire.RecordInboundContactAsync(ct).ConfigureAwait(false);

            var lastMsg = activeThread!.Messages[^1];
            obs.ContactMessage = lastMsg.Content;
            obs.WasConversationCycle = true;

            // Admin commands bypass conversation entirely
            if (AdminCommandHandler.IsAdminCommand(lastMsg.Content))
            {
                _log.LogInformation("Admin command detected: {Content}", lastMsg.Content);
                await _conversations.CloseThreadAsync(activeThread.Id, ct).ConfigureAwait(false);
                await _adminCommands.HandleAsync(lastMsg.Content, ct).ConfigureAwait(false);
                return;
            }

            // If we already evaluated this exact message and decided NO, don't re-ask.
            if (_gateState.LastEvaluatedMessageAt.HasValue && lastMsg.SentAt == _gateState.LastEvaluatedMessageAt.Value)
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

        // Phase 3: Reactive sharing
        if (await _outreach.TryReactiveShareAsync(perceptions, charState, ct).ConfigureAwait(false))
        {
            obs.OutreachOutcome = "reactive-share";
            return;
        }

        // Phase 4: Context snapshot + inner thought — delegated to phases
        var snapshot = await _contextBuilder.BuildContextSnapshotAsync(perceptions, ct, emotionalState).ConfigureAwait(false);

        // World Layer: every Nth cycle, seed the inner thought with experiential context
        _cycleCount++;
        var isWorldCycle = _worldSeed.ShouldSeedThisCycle(_cycleCount);
        if (isWorldCycle)
        {
            var weatherContext = perceptions
                .FirstOrDefault(p => p.SourceName == "weather")?.Summary;
            var seed = _worldSeed.GenerateSeed(
                DateTimeOffset.Now, weatherContext, charState.Occupation);
            snapshot.WorldSeed = seed;
            _log.LogInformation("World seed (cycle {Cycle}): {Seed}", _cycleCount, seed);
        }

        // Associative anchor: inject the previous thought's anchor as a creative fragment.
        // This enables drift (bookstore → pages → turning points → ...) instead of
        // thematic repetition (warmth → warmth → warmth).
        if (_lastAssociativeAnchor is not null && snapshot.WorldSeed is null)
        {
            snapshot.WorldSeed = $"The last thing lingering in your mind: {_lastAssociativeAnchor}";
        }

        var (thought, reflection, valence) = await _innerThought.RunAsync(snapshot, ct).ConfigureAwait(false);
        obs.InnerThought = thought;
        obs.Reflection = reflection;
        obs.RelationalValence = valence;

        // Store thought with reflection appended
        // World-seeded thoughts tagged distinctly for retrieval prioritization
        var contentForStorage = reflection is not null
            ? $"{thought} [reflection: {reflection}]"
            : thought;

        await _persist.SaveAsync(new MemoryRecord
        {
            Type        = MemoryType.InnerThought,
            Content     = contentForStorage,
            RelationalValence = valence,
            Importance  = valence > (float)_aniOptions.ValenceTriggerThreshold ? 0.8f : 0.3f,
            SourceName  = isWorldCycle ? SourceNames.WorldExperience : null,
            OccurredAt  = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        _log.LogInformation("{Type} (valence={Valence:F2}): {Thought}",
            isWorldCycle ? "World experience" : "Inner thought", valence, thought);
        if (reflection is not null)
            _log.LogInformation("Reflection: {Reflection}", reflection);

        // Extract associative anchor for the next cycle's creative drift
        if (_mlClassifier is not null)
        {
            try
            {
                var anchors = await _mlClassifier.ExtractAnchorsAsync(thought, 1, ct).ConfigureAwait(false);
                _lastAssociativeAnchor = anchors.FirstOrDefault();
                if (_lastAssociativeAnchor is not null)
                {
                    _log.LogDebug("Associative anchor: {Anchor}", _lastAssociativeAnchor);

                    // Store anchor on the most recent contribution for dashboard visualization
                    var recentContribs = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);
                    var latest = recentContribs
                        .Where(c => c.SourceContent.StartsWith(thought.Length > 50 ? thought[..50] : thought, StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();
                    if (latest is not null)
                    {
                        latest.AssociativeAnchor = _lastAssociativeAnchor;
                        await _persist.SaveEmotionalContributionAsync(latest, ct).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                _lastAssociativeAnchor = null;
            }
        }

        // Phase 4b: Emotional shift from inner thought
        var preShiftW = emotionalState.Warmth;
        var preShiftE = emotionalState.Energy;
        var preShiftWo = emotionalState.Worry;
        var preShiftP = emotionalState.Playfulness;

        await _emotional.ApplyEmotionalShiftAsync(emotionalState, thought, ct,
            isAmbientCycle: true, category: ImpactCategory.Ambient).ConfigureAwait(false);

        // Re-read emotional state after shift for emergence observation deltas
        var postShift = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var postContributions = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);
        postShift.ComputeFromContributions(postContributions);
        obs.WarmthDelta = postShift.Warmth - preShiftW;
        obs.EnergyDelta = postShift.Energy - preShiftE;
        obs.WorryDelta = postShift.Worry - preShiftWo;
        obs.PlayfulnessDelta = postShift.Playfulness - preShiftP;

        // Feature 32: Periodic reflection synthesis (Park et al.)
        // Runs every N cycles — synthesizes recent memories into higher-order observations.
        await _reflection.TryRunAsync(snapshot.CharacterState, ct).ConfigureAwait(false);

        // Phase 5: Desire update
        // Feature 33 (Liu et al.): Motivation score modulates desire drift
        var motivation = MotivationScorer.Score(valence, obs.Severity, postShift);
        await _desire.ApplyDriftAsync(motivation, ct).ConfigureAwait(false);

        if (valence > (float)_aniOptions.ValenceTriggerThreshold)
            await _desire.AddTriggerAsync(
                TriggerType.SpontaneousThought, valence,
                $"thought: {thought[..Math.Min(60, thought.Length)]}", ct).ConfigureAwait(false);

        var desireAfter = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        obs.DesireToConnect = desireAfter.DesireToConnect;

        // Phase 6: Outreach — only if desire crosses threshold
        if (activeThread is not null)
        {
            if (hasUnreadFromContact && _gateState.LastEvaluatedMessageAt.HasValue &&
                await _desire.ShouldReachOutAsync(ct).ConfigureAwait(false))
            {
                _log.LogInformation("Desire built after choosing silence — reconsidering reply");
                await _conversationReply.RunConversationReplyAsync(activeThread, perceptions, ct, emotionalState,
                    isReconsideration: true).ConfigureAwait(false);
            }
            else
            {
                _log.LogDebug("Active conversation — suppressing ambient outreach");
                obs.OutreachOutcome = "suppress-conversation";
            }
            return;
        }

        if (_conversationReply.IsWithdrawn)
        {
            _log.LogInformation("Outreach suppressed: withdrawal active");
            obs.OutreachOutcome = "withdrawn";
            return;
        }

        if (!await _desire.ShouldReachOutAsync(ct).ConfigureAwait(false))
        {
            _log.LogDebug("Desire below threshold — no outreach this cycle");
            obs.OutreachOutcome = "no-desire";

            var desireState = await _desire.GetStateAsync(ct).ConfigureAwait(false);
            if (desireState.DesireToConnect > 0.3f)
            {
                obs.ChoseSilence = true;
                await _outreach.RecordSilenceChoiceAsync(desireState, emotionalState, ct).ConfigureAwait(false);
            }

            return;
        }

        obs.DesireThresholdCrossed = true;

        // Feature 27: Hard runtime gates — outreach continuity checks
        var outreachCtx = snapshot.OutreachContext;
        if (outreachCtx is not null)
        {
            if (outreachCtx.UnansweredCount >= _aniOptions.MaxUnansweredBeforeSilence)
            {
                _log.LogInformation(
                    "Outreach suppressed: {Count} unanswered messages (limit={Limit}) — waiting for reply",
                    outreachCtx.UnansweredCount, _aniOptions.MaxUnansweredBeforeSilence);
                obs.OutreachOutcome = "suppress-gates";
                return;
            }

            if (outreachCtx.TimeSinceLastSend.HasValue &&
                outreachCtx.TimeSinceLastSend.Value.TotalMinutes < _aniOptions.MinSendGapMinutes)
            {
                _log.LogInformation(
                    "Outreach suppressed: only {Minutes:F0} min since last send (minimum={Gap} min)",
                    outreachCtx.TimeSinceLastSend.Value.TotalMinutes, _aniOptions.MinSendGapMinutes);
                obs.OutreachOutcome = "suppress-gates";
                return;
            }
        }

        await _outreach.RunOutreachAsync(snapshot, thought, ct).ConfigureAwait(false);
        obs.OutreachOutcome = "send";

        } // end try
        finally
        {
            try
            {
                var observation = obs.Build();
                await _emergence.OnCycleCompleteAsync(observation, default).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Emergence observer failed — cycle unaffected");
            }
        }
    }

    // ── Static forwarding methods for backward compatibility ────────────────
    // Tests reference CognitiveCycleProcessor.StaticMethod() — these delegate
    // to ConversationFeatureDetector where the logic now lives.

    internal static bool DetectCareGivingIntent(string message)
        => ConversationFeatureDetector.DetectCareGivingIntent(message);

    internal static bool DetectHurtIntent(string message)
        => ConversationFeatureDetector.DetectHurtIntent(message);

    internal static List<EmotionalContribution> BuildLexicalAnchorContributions(
        string message, CharacterStateDoc charState)
        => ConversationFeatureDetector.BuildLexicalAnchorContributions(message, charState);

    internal static bool EndsWithDirectQuestion(string message)
        => ConversationFeatureDetector.EndsWithDirectQuestion(message);

    internal static bool ContainsMemoryReferencingLanguage(string message)
        => ConversationFeatureDetector.ContainsMemoryReferencingLanguage(message);

    internal static bool ContainsThirdPersonReference(string message, string contactName)
        => OutreachPhase.ContainsThirdPersonReference(message, contactName);
}
