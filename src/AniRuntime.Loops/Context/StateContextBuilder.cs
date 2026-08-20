using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Context;

/// <summary>
/// Production implementation of <see cref="IStateContextBuilder"/>.
/// Extracted from <c>ContextBuilder</c> 2026-05-19 as the fifth and final
/// sub-builder of the SRP decomposition (`ANI-Testability-Architecture-Plan.md`
/// §2). After this extraction <c>ContextBuilder</c> becomes a thin
/// orchestrator composing five sub-builder outputs into a
/// <see cref="ContextSnapshot"/>.
///
/// Owns the state-pull surface previously inline in
/// <c>ContextBuilder.BuildContextSnapshotAsync</c> plus two helpers
/// (<c>BuildOutreachContext</c> Feature 27 and <c>BuildThoughtDiversityNudge</c>
/// Feature 41).
/// </summary>
public sealed class StateContextBuilder : IStateContextBuilder
{
    private readonly IStateStore                     _state;
    private readonly IMemoryAnalytics                _analytics;
    private readonly DesireEngine                    _desire;
    private readonly IDiagnosticService              _diagnostic;
    private readonly ILogger<StateContextBuilder>    _log;

    public StateContextBuilder(
        IStateStore                     state,
        IMemoryAnalytics                analytics,
        DesireEngine                    desire,
        IDiagnosticService              diagnostic,
        ILogger<StateContextBuilder>    log)
    {
        _state      = state;
        _analytics  = analytics;
        _desire     = desire;
        _diagnostic = diagnostic;
        _log        = log;
    }

    public async Task<StateContextResult> BuildAsync(
        IReadOnlyList<MemoryRecord>     recentMemory,
        EmotionalState?                 emotionalStateOverride,
        CancellationToken               ct)
    {
        var charState   = await _state.GetCharacterStateAsync(ct).ConfigureAwait(false);
        var desireState = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        var openLoops   = (await _analytics.GetOpenLoopsAsync(ct).ConfigureAwait(false)).ToList();

        var emotionalState = emotionalStateOverride
            ?? await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);

        // F-1 Phase 8e (2026-08-19): BuildOutreachContext returns
        // IRecentOutreachContextEnvelope for producer-boundary provenance.
        // Unwrap immediately — StateContextResult / ContextSnapshot / prompt
        // commands downstream continue to consume the bare record.
        //
        // F-1 Phase 9 (2026-08-20): call LogProvenance at the producer
        // emit site (here rather than inside BuildOutreachContext, which
        // is static and has no logger instance). Emits F1_PROVENANCE
        // structured-log line per cycle.
        var outreachContextEnv    = BuildOutreachContext(recentMemory, desireState);
        outreachContextEnv.LogProvenance(_log);
        var thoughtDiversityNudge = BuildThoughtDiversityNudge();

        return new StateContextResult(
            CharacterState:         charState,
            DesireState:            desireState,
            EmotionalState:         emotionalState,
            OpenLoops:              openLoops,
            OutreachContext:        outreachContextEnv.Content,
            ThoughtDiversityNudge:  thoughtDiversityNudge);
    }

    /// <summary>
    /// Feature 41: When PERCEPTION-ANCHOR is active in the latest diagnostic
    /// report, generate a gentle curiosity redirect framed as self-discovery
    /// (not rejection) to avoid negative emotional response.
    /// </summary>
    internal string? BuildThoughtDiversityNudge()
    {
        var report = _diagnostic.LatestReport;
        if (report is null) return null;

        var anchor = report.Findings
            .FirstOrDefault(f => f.Code == "PERCEPTION-ANCHOR" &&
                                 f.Severity >= DiagnosticSeverity.Info);
        if (anchor is null) return null;

        return "You've been circling the same thought for a while. " +
               "Your mind has more in it than this. " +
               "What else has been sitting quietly that you haven't explored yet?";
    }

    /// <summary>
    /// Feature 27: Assembles outreach continuity context from recent episodic
    /// memory. Determines which outreach messages were answered by checking
    /// if any conversation or inbound contact occurred after each outreach
    /// record.
    ///
    /// <para>
    /// F-1 Phase 8e (2026-08-19): returns <see cref="IRecentOutreachContextEnvelope"/>
    /// for producer-boundary provenance. The immediate caller
    /// (<see cref="BuildAsync"/>) unwraps via <c>.Content</c> to construct
    /// <c>StateContextResult</c>. Downstream contracts unchanged.
    /// </para>
    /// </summary>
    internal static IRecentOutreachContextEnvelope BuildOutreachContext(
        IReadOnlyList<MemoryRecord> recentMemory, DesireState desireState)
    {
        const string outreachPrefix = "I reached out to ";
        var outreachRecords = recentMemory
            .Where(m => m.Type == MemoryType.Episodic && m.Content.StartsWith(outreachPrefix))
            .OrderByDescending(m => m.OccurredAt)
            .Take(5)
            .ToList();

        // Conversation records indicate the contact replied at some point.
        var conversationTimes = recentMemory
            .Where(m => m.Type == MemoryType.Episodic && m.Content.StartsWith("Conversation ("))
            .Select(m => m.OccurredAt)
            .ToList();

        var lastContactReply = desireState.LastContactInbound;

        var records = new List<OutreachRecord>();
        foreach (var outreach in outreachRecords)
        {
            var wasAnswered = lastContactReply > outreach.OccurredAt ||
                              conversationTimes.Any(t => t > outreach.OccurredAt);

            // Extract message text: "I reached out to Mark: "message here""
            var colonIdx = outreach.Content.IndexOf(": \"", StringComparison.Ordinal);
            var msgText = colonIdx >= 0
                ? outreach.Content[(colonIdx + 3)..].TrimEnd('"')
                : outreach.Content[outreachPrefix.Length..];

            records.Add(new OutreachRecord
            {
                Message     = msgText.Trim(),
                SentAt      = outreach.OccurredAt,
                WasAnswered = wasAnswered,
            });
        }

        // Count consecutive unanswered from most recent.
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

        return new RecentOutreachContextEnvelope
        {
            Context = new RecentOutreachContext
            {
                RecentMessages             = records,
                UnansweredCount            = unanswered,
                TimeSinceLastSend          = timeSinceLastSend,
                TimeSinceLastContactReply  = timeSinceReply,
            },
        };
    }
}
