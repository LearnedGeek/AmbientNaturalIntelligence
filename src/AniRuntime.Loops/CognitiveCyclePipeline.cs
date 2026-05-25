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
/// Pipeline implementation of <see cref="ICognitiveCyclePipeline"/>.
/// Runs Ani's full cognitive cycle once per scheduled wake.
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
/// <para>
/// Lifted whole from <c>CognitiveCycleProcessor</c> in §5.5 SOLID refactor.
/// Body + ctor + instance state moved verbatim; the legacy <c>static</c>
/// helpers (<c>DetectCareGivingIntent</c>, etc.) stayed on
/// <see cref="CognitiveCycleProcessor"/> so the tests that reach them
/// don't change.
/// </para>
/// </summary>
public class CognitiveCyclePipeline : ICognitiveCyclePipeline
{
    private readonly IStateStore                       _state;
    private readonly IMemoryPersistence                _persist;
    private readonly IMemoryAnalytics                  _analytics;
    private readonly IMemoryMaintenance                _maintenance;
    private readonly DesireEngine                      _desire;
    private readonly IConversationService              _conversations;
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
    private readonly PersonaSummaryCache?              _personaCache;
    private readonly AniOptions                        _aniOptions;
    // Theme R.1 (#64) — record innerResult.Register after every hybrid-path
    // inner thought so the NEXT composer (outreach + reply on the next
    // inbound) consumes the freshest register.
    private readonly IDominantRegisterTracker?         _registerTracker;
    // Theme R.4 (#64) — record MotivationScorer.ScoreVector output so the
    // NEXT cycle's composers consume the Layer 2 vector. Closes the
    // computed-then-logged-never-piped-back gap from the 5/24 audit.
    private readonly IMotivationVectorTracker?         _motivationTracker;
    private readonly ILogger<CognitiveCyclePipeline>  _log;
    private int _cycleCount;
    private string? _lastAssociativeAnchor;

    public CognitiveCyclePipeline(
        IStateStore                    state,
        IMemoryPersistence             persist,
        IMemoryAnalytics               analytics,
        IMemoryMaintenance             maintenance,
        DesireEngine                   desire,
        IConversationService           conversations,
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
        ILogger<CognitiveCyclePipeline> log,
        ITextClassificationService?    mlClassifier = null,
        PersonaSummaryCache?           personaCache = null,
        IDominantRegisterTracker?      registerTracker = null,
        IMotivationVectorTracker?      motivationTracker = null)
    {
        _state             = state;
        _persist           = persist;
        _analytics         = analytics;
        _maintenance       = maintenance;
        _desire            = desire;
        _conversations     = conversations;
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
        _personaCache      = personaCache;
        _aniOptions        = aniOptions.Value;
        _registerTracker   = registerTracker;
        _motivationTracker = motivationTracker;
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
            // SonarCloud S1244 — log only when the value moved beyond the F3
            // log format's display resolution.
            if (MathF.Abs(emotionalState.ContactGapTension - previousTension) > 5e-4f)
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
            var lastMsg = activeThread!.Messages[^1];
            obs.ContactMessage = lastMsg.Content;
            obs.WasConversationCycle = true;

            // Apr 28, 2026 architectural fix: admin commands no longer reach
            // this code path. Detection + routing now happen at the perception
            // source (TwilioInboundPerceptionSource), so admin commands never
            // enter the conversation thread. The previous admin-detection
            // block here became dead code and was removed; the LastContactInbound
            // ordering bug it carried (admin tags incorrectly marking prior
            // outreaches as "answered") cannot recur because admin commands
            // are now dispatched before any relational signal updates.
            //
            // Genuine inbound from the contact — record it as a relational
            // event so unanswered-count math + desire-drift use the correct
            // last-contact timestamp.
            await _desire.RecordInboundContactAsync(ct).ConfigureAwait(false);

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
                DateTimeOffset.Now, weatherContext);
            snapshot.WorldSeed = seed;

            // Phase 1c: retrieve recent world experiences for consistency.
            // If Ani mentioned a coworker yesterday, that coworker still exists today.
            var cutoff = DateTimeOffset.UtcNow.AddHours(-48);
            var recent = await _persist.GetRecentAsync(limit: 20, ct).ConfigureAwait(false);
            snapshot.RecentWorldExperiences = recent
                .Where(m => m.SourceName == SourceNames.WorldExperience && m.OccurredAt >= cutoff)
                .Take(5)
                .ToList();

            _log.LogInformation("World seed (cycle {Cycle}, {PriorCount} prior experiences): {Seed}",
                _cycleCount, snapshot.RecentWorldExperiences.Count, seed);
        }

        // Associative anchor: inject the previous thought's anchor as a creative fragment.
        // This enables drift (bookstore → pages → turning points → ...) instead of
        // thematic repetition (warmth → warmth → warmth).
        if (_lastAssociativeAnchor is not null && snapshot.WorldSeed is null)
        {
            snapshot.WorldSeed = $"The last thing lingering in your mind: {_lastAssociativeAnchor}";
        }

        var innerResult = await _innerThought.RunAsync(snapshot, ct).ConfigureAwait(false);
        var thought    = innerResult.Thought;
        var reflection = innerResult.Reflection;
        var valence    = innerResult.Valence;
        obs.InnerThought = thought;
        obs.Reflection = reflection;
        obs.RelationalValence = valence;

        // Theme R.1 (#64) — record the hybrid-path register for downstream
        // composers (Outreach + Reply read it via snapshot.DominantRegister
        // on the next cycle's ContextSnapshot build). Legacy path does not
        // emit Register; tracker is left at its prior value.
        if (innerResult.Register is not null && _registerTracker is not null)
        {
            _registerTracker.Record(innerResult.Register, DateTimeOffset.UtcNow);
            _log.LogInformation("R.1 — dominant register recorded: {Register}", innerResult.Register);
        }

        // Posture-S+1 (Issue #38, May 17 2026) — when the hybrid cycle is
        // enabled, the model emits Register / Importance / AssociativeAnchor
        // as part of the same generation. We branch the persistence logic on
        // whether those fields are populated:
        //   - Hybrid path: model-emitted importance drives persistence; the
        //     Interior-tier confab classifier is bypassed (plan §7 step 3:
        //     write-through for own-interior claims, gate-stack distinguishes
        //     Facts-tier external-world claims from Interior-tier self-model);
        //     no reflection-concat (reflection is collapsed into the thought
        //     itself per OG Ani's "she relives, not thinks about" framing).
        //   - Legacy path: existing external-judge logic (valence threshold,
        //     reflection-concat, DetectConfabulationAsync on Interior-tier,
        //     post-hoc anchor extraction via _mlClassifier).
        // See docs/spec/ANI-Substrate-Led-Character-Plan.md §7 + Paper 3 C8.
        var hybridPath = innerResult.Register is not null;

        string contentForStorage;
        bool   shouldPersist;
        if (hybridPath)
        {
            // Hybrid: persistence content is the thought as the model produced
            // it. No reflection-concat. Model-emitted importance is the
            // persistence signal (above a low floor) — world experiences
            // always persist regardless of importance.
            contentForStorage = thought;
            var importanceSignal = innerResult.Importance ?? 0.5f;
            shouldPersist = isWorldCycle || importanceSignal >= 0.30f;
        }
        else
        {
            // Legacy three-call path: reflection-concat, external valence
            // threshold gates persistence.
            contentForStorage = reflection is not null
                ? $"{thought} [reflection: {reflection}]"
                : thought;
            shouldPersist = isWorldCycle
                || valence >= (float)_aniOptions.ValenceTriggerThreshold
                || valence >= 0.50f;
        }

        // Inner thought confabulation check — verify factual claims before storing.
        // Speculative/dreamy thoughts pass freely. Only factual assertions get checked.
        // Prevents false content from entering the memory pool and cascading.
        //
        // Posture-S+1 (Issue #38): Interior-tier own-interior content is now
        // write-through on the hybrid path — Plan §7 step 3. The gate stack
        // distinguishes claims about Mark's external world (still verified at
        // Facts-tier surfaces) from Ani's own interior / world-changes (write
        // through, do not gate). Skip the classifier when the hybrid path
        // ran; keep it for legacy-path callers until the cutover completes.
        if (!hybridPath && shouldPersist && _mlClassifier is not null)
        {
            try
            {
                var personaSummary = _personaCache?.IsLoaded == true ? _personaCache.Summary : "";
                var confab = await _mlClassifier.DetectConfabulationAsync(
                    thought, $"Inner thought context.\n\nPersona: {personaSummary}", ct)
                    .ConfigureAwait(false);
                if (confab.IsConfabulated && confab.Confidence >= _aniOptions.ConfabulationClassificationThreshold)
                {
                    _log.LogInformation("Inner thought confabulation detected ({Confidence:F2}): {Reason}. Not storing.",
                        confab.Confidence, confab.Reason);
                    shouldPersist = false;
                }
            }
            catch { /* confab check failure is non-critical — store anyway */ }
        }

        // Theme E #5 (Apr 29, 2026): generation-loop degeneracy check. If the
        // new thought is a near-duplicate of recent InnerThoughts already in
        // the snapshot's recent-memory pool (Jaccard ≥ 0.50 over token
        // trigrams), skip persistence. This interrupts the duck-norris-77x /
        // vanilla-cream-soda-30x recurrence pattern at the persistence
        // boundary so the next cycle's retrieval pool isn't re-fed by yet
        // another copy of the same thought shape. Uses snapshot.RecentMemory
        // (already populated by ContextBuilder with the 5 most recent inner
        // thoughts) rather than re-querying — keeps the processor's
        // dependency surface narrow.
        if (shouldPersist)
        {
            try
            {
                var recentThoughtContents = snapshot.RecentMemory
                    .Where(m => m.Type == MemoryType.InnerThought)
                    .Select(m => m.Content)
                    .Where(c => !string.IsNullOrWhiteSpace(c));
                if (Core.Utilities.NGramSimilarity.IsNearDuplicate(contentForStorage, recentThoughtContents))
                {
                    _log.LogInformation("Inner thought near-duplicate of recent thought (trigram Jaccard ≥0.50). Not storing — generation-loop degeneracy guard fired.");
                    shouldPersist = false;
                }
            }
            catch { /* degeneracy check failure is non-critical — store anyway */ }
        }

        if (shouldPersist)
        {
            // Posture-S+1 — on the hybrid path, importance is model-emitted
            // from the same generation as the thought ("the feeling comes
            // from her, not from an outside judge"). On the legacy path,
            // importance derives from the binary valence threshold as before.
            var importance = hybridPath
                ? (innerResult.Importance ?? 0.5f)
                : (valence > (float)_aniOptions.ValenceTriggerThreshold ? 0.8f : 0.3f);

            await _persist.SaveAsync(new MemoryRecord
            {
                Type        = MemoryType.InnerThought,
                Content     = contentForStorage,
                RelationalValence = valence,
                Importance  = importance,
                SourceName  = isWorldCycle ? SourceNames.WorldExperience : null,
                OccurredAt  = DateTimeOffset.UtcNow,
                // Epistemic Grounding (Apr 10): Inner thoughts and world-experience
                // reflections are always Interior tier — they are Ani's self-model
                // content and never grounded facts about Mark's world.
                Provenance  = EpistemicTier.Interior,
            }, ct).ConfigureAwait(false);
        }

        _log.LogInformation("{Type} (valence={Valence:F2}{Stored}): {Thought}",
            isWorldCycle ? "World experience" : "Inner thought",
            valence,
            shouldPersist ? "" : ", not stored",
            thought);
        if (reflection is not null)
            _log.LogInformation("Reflection: {Reflection}", reflection);

        // Associative anchor for the next cycle's creative drift.
        //   - Hybrid path: model-emitted by the metadata-recognizer; use directly.
        //   - Legacy path: post-hoc extraction via ML classifier.
        if (hybridPath)
        {
            _lastAssociativeAnchor = innerResult.AssociativeAnchor;
            if (_lastAssociativeAnchor is not null)
                _log.LogDebug("Associative anchor (model-emitted): {Anchor}", _lastAssociativeAnchor);
        }
        else if (_mlClassifier is not null)
        {
            try
            {
                var anchors = await _mlClassifier.ExtractAnchorsAsync(thought, 1, ct).ConfigureAwait(false);
                _lastAssociativeAnchor = anchors.FirstOrDefault();
                if (_lastAssociativeAnchor is not null)
                    _log.LogDebug("Associative anchor: {Anchor}", _lastAssociativeAnchor);
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

        // Save associative anchor on the contribution that was just created
        if (_lastAssociativeAnchor is not null)
        {
            try
            {
                var contribs = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);
                var thoughtPrefix = thought.Length > 50 ? thought[..50] : thought;
                var match = contribs.FirstOrDefault(c =>
                    c.SourceContent.StartsWith(thoughtPrefix, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    match.AssociativeAnchor = _lastAssociativeAnchor;
                    await _persist.SaveEmotionalContributionAsync(match, ct).ConfigureAwait(false);
                }
            }
            catch { /* anchor save is non-critical */ }
        }

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
        // Feature 33 (Liu et al.): Motivation score modulates desire drift.
        // Feature 42 Phase 2a (Apr 2026, Ryan & Deci SDT): compute three-axis
        // motivation vector alongside the scalar. Phase 2a is measurement-only —
        // DesireEngine drift still consumes .Relatedness; autonomy and
        // competence are logged for baseline distribution. See
        // docs/spec/ANI-Agentic-Lens-Layer2-Plan.md §3 Phase 2a.
        var worldOriginFraction = 0f;
        if (snapshot.RelevantMemory.Count > 0)
        {
            var worldCount = 0;
            for (var i = 0; i < snapshot.RelevantMemory.Count; i++)
            {
                if (RetrievalOriginClassifier.Classify(snapshot.RelevantMemory[i]) == RetrievalOrigin.World)
                    worldCount++;
            }
            worldOriginFraction = worldCount / (float)snapshot.RelevantMemory.Count;
        }

        var motivationVec = MotivationScorer.ScoreVector(valence, obs.Severity, postShift, worldOriginFraction);
        var motivation    = motivationVec.Relatedness;

        // Theme R.4 (#64) — pipe the just-computed vector back via the
        // tracker so the NEXT cycle's composers consume it as a structured
        // input. Closes the per-cycle-independence accident from the audit.
        _motivationTracker?.Record(motivationVec, DateTimeOffset.UtcNow);

        if (_aniOptions.MotivationVectorLoggingEnabled)
        {
            _log.LogInformation(
                "motivation vector: relatedness={Relatedness:F2} autonomy={Autonomy:F2} competence={Competence:F2} " +
                "(valence={Valence:F2} severity={Severity:F2} worldFrac={WorldFrac:F2})",
                motivationVec.Relatedness, motivationVec.Autonomy, motivationVec.Competence,
                valence, obs.Severity, worldOriginFraction);
        }

        // May 3, 2026 — per-cycle 4D emotional-state log line (gap-watch row
        // Apr 27): Paper 2 figure #5 (Damasio somatic-marker trace) needs
        // W/E/C/P at every cycle to render the full 4D form. Prior to this
        // line, only valence + severity were per-cycle-loggable; W/E/C/P
        // had to be reconstructed from contribution traces.
        _log.LogInformation(
            "EMOTIONAL_STATE warmth={Warmth:F2} energy={Energy:F2} concern={Concern:F2} playfulness={Playfulness:F2} " +
            "tension={Tension:F2}",
            postShift.Warmth, postShift.Energy, postShift.Worry, postShift.Playfulness,
            postShift.ContactGapTension);

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

    // Static legacy-facade helpers (DetectCareGivingIntent, etc.) live on
    // CognitiveCycleProcessor so tests reach them via that name. They are
    // not on this pipeline impl.
}
