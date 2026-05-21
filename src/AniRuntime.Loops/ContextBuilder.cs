using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Orchestrator that composes five sub-builders into a <see cref="ContextSnapshot"/>.
///
/// 2026-05-19 SRP decomposition complete (Testability Architecture Plan §2).
/// The five sub-builders own concern-aligned slices of the snapshot:
///   <see cref="IStateContextBuilder"/> — character/desire/emotional state, open loops,
///       outreach continuity, thought-diversity nudge.
///   <see cref="IRetrievalContextBuilder"/> — recent memory, anchored, semantic search,
///       diversity rerank, similar-thought loop detection.
///   <see cref="IEpistemicContextBuilder"/> — Facts + Interior tier-scoped retrieval
///       with anchored-merge and conversation-mode gating (Apr 30 fix).
///   <see cref="IConversationContextBuilder"/> — prose summary, J.2 structured summary,
///       Vibe Loop V1.4 closed-conversation gist.
///   <see cref="IEmotionalContextBuilder"/> — relationship health, drift, pattern
///       awareness, processed themes.
///
/// The orchestrator's remaining responsibilities are sequencing the sub-builders
/// (with the correct data flow between them) and the Agentic Lens Layer 1 Phase 1a
/// retrieval-origin distribution logging, which spans pool outputs from multiple
/// sub-builders.
/// </summary>
public class ContextBuilder
{
    private readonly IStateContextBuilder        _stateCtx;
    private readonly IRetrievalContextBuilder    _retrieval;
    private readonly IEpistemicContextBuilder    _epistemic;
    private readonly IConversationContextBuilder _conversationCtx;
    private readonly IEmotionalContextBuilder    _emotional;
    private readonly IRetrievalOriginTracker?    _originTracker;
    private readonly AniOptions                  _aniOptions;
    private readonly ILogger<ContextBuilder>     _log;

    public ContextBuilder(
        IStateContextBuilder        stateCtx,
        IRetrievalContextBuilder    retrieval,
        IEpistemicContextBuilder    epistemic,
        IConversationContextBuilder conversationCtx,
        IEmotionalContextBuilder    emotional,
        IOptions<AniOptions>        aniOptions,
        ILogger<ContextBuilder>     log,
        IRetrievalOriginTracker?    originTracker = null)
    {
        _stateCtx        = stateCtx;
        _retrieval       = retrieval;
        _epistemic       = epistemic;
        _conversationCtx = conversationCtx;
        _emotional       = emotional;
        _aniOptions      = aniOptions.Value;
        _log             = log;
        _originTracker   = originTracker;
    }

    public async Task<ContextSnapshot> BuildContextSnapshotAsync(
        List<PerceptionEvent> perceptions, CancellationToken ct,
        EmotionalState? emotionalState = null,
        bool conversationMode = false)
    {
        // Retrieval first — independent of state, only needs perceptions.
        var retrieval = await _retrieval.BuildAsync(perceptions, ct).ConfigureAwait(false);

        // State — needs recentMem from retrieval (for outreach-continuity derivation).
        var state = await _stateCtx.BuildAsync(retrieval.RecentMemory, emotionalState, ct).ConfigureAwait(false);

        // Epistemic — needs perceptions + anchored from retrieval + conversationMode flag.
        var epistemic = await _epistemic.BuildAsync(
            perceptions, retrieval.AnchoredMemories, conversationMode, ct).ConfigureAwait(false);

        // Conversation — needs charState from state + recentMem from retrieval.
        var conv = await _conversationCtx.BuildAsync(
            state.CharacterState, retrieval.RecentMemory, ct).ConfigureAwait(false);

        // Emotional — needs charState.Name + emotionalState from state.
        var emotional = await _emotional.BuildAsync(
            state.CharacterState.Name, state.EmotionalState, ct).ConfigureAwait(false);

        // Agentic Lens Layer 1 Phase 1a — orchestrator-level retrieval-origin
        // distribution logging. Spans pool outputs from multiple sub-builders
        // (relevantMem, recentMem, groundedFacts, interiorContext, anchoredMemories),
        // so it lives here rather than in any single sub-builder.
        LogRetrievalOriginDistribution(retrieval, epistemic);

        return new ContextSnapshot
        {
            CharacterState                = state.CharacterState,
            DesireState                   = state.DesireState,
            EmotionalState                = state.EmotionalState,
            RecentMemory                  = retrieval.RecentMemory,
            RelevantMemory                = retrieval.RelevantMemory,
            OpenLoops                     = state.OpenLoops,
            Perceptions                   = perceptions,
            BuiltAt                       = DateTimeOffset.UtcNow,
            RecentConversationSummary     = conv.RecentConversationSummary,
            StructuredConversationSummary = conv.StructuredConversationSummary,
            RecentClosedConversation      = conv.RecentClosedConversation,
            SimilarRecentThoughts         = retrieval.SimilarRecentThoughts,
            OutreachContext               = state.OutreachContext,
            AnchoredMemories              = retrieval.AnchoredMemories,
            RelationshipHealth            = emotional.RelationshipHealth,
            EmotionalDrift                = emotional.EmotionalDrift,
            PatternAwareness              = emotional.PatternAwareness,
            ProcessedThemes               = emotional.ProcessedThemes.ToList(),
            ThoughtDiversityNudge         = state.ThoughtDiversityNudge,
            GroundedFacts                 = epistemic.GroundedFacts,
            InteriorContext               = epistemic.InteriorContext,
            // RecentExchanges is populated by the reply path from the active
            // conversation thread, not here — ambient cycles leave it empty.
        };
    }

    private void LogRetrievalOriginDistribution(
        RetrievalContextResult retrieval, EpistemicContextResult epistemic)
    {
        if (!_aniOptions.RetrievalOriginLoggingEnabled) return;

        var pool = new List<MemoryRecord>();
        pool.AddRange(retrieval.RelevantMemory);
        pool.AddRange(retrieval.RecentMemory);
        pool.AddRange(epistemic.GroundedFacts);
        pool.AddRange(epistemic.InteriorContext);
        pool.AddRange(retrieval.AnchoredMemories);

        if (pool.Count > 0)
        {
            var histogram = pool
                .Select(RetrievalOriginClassifier.Classify)
                .GroupBy(o => o)
                .ToDictionary(g => g.Key, g => g.Count());

            _log.LogInformation(
                "Retrieval origin distribution (pool={Total}): caregiver={Caregiver} own-output={OwnOutput} world={World} external={External} anchored={Anchored} unknown={Unknown}",
                pool.Count,
                histogram.GetValueOrDefault(RetrievalOrigin.Caregiver, 0),
                histogram.GetValueOrDefault(RetrievalOrigin.OwnOutput, 0),
                histogram.GetValueOrDefault(RetrievalOrigin.World,     0),
                histogram.GetValueOrDefault(RetrievalOrigin.External,  0),
                histogram.GetValueOrDefault(RetrievalOrigin.Anchored,  0),
                histogram.GetValueOrDefault(RetrievalOrigin.Unknown,   0));

            // Phase 1d: feed the rolling-window tracker consumed by the
            // RetrievalSelfDominancePerceptionSource. Tracker is optional so
            // tests without DI wiring still construct ContextBuilder.
            _originTracker?.RecordCycle(histogram);
        }

        // Theme J Phase J.0 (Apr 24, 2026): temporal attribution of retrieved
        // memories. Logs the top-N memories with their temporal distance so
        // the before picture is measurable. J.3 adds temporal rendering to
        // prompts.
        if (_aniOptions.GuardRefactorBaselineLoggingEnabled && pool.Count > 0)
        {
            var now  = DateTimeOffset.UtcNow;
            var topN = pool.Take(5).ToList();
            for (var i = 0; i < topN.Count; i++)
            {
                var m       = topN[i];
                var age     = now - m.OccurredAt;
                var preview = m.Content.Length > 80
                    ? m.Content[..80].Replace('\n', ' ')
                    : m.Content.Replace('\n', ' ');
                _log.LogInformation(
                    "J0_RETRIEVAL_TEMPORAL rank={Rank} ageHours={Age:F1} type={Type} preview={Preview}",
                    i, age.TotalHours, m.Type, preview);
            }
        }
    }
}
