using Microsoft.Extensions.Logging;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 9 (2026-08-20) — the first consumer of
/// <see cref="IProvenancedContent{T}"/> provenance. Emits a structured
/// Serilog log line at every envelope emit site so producer-boundary
/// activity is observable in journal logs without changing any
/// downstream signature.
///
/// <para>
/// Phase 8a–8f established the producer-boundary surface across 14 F-1
/// producers; all 6 Phase 8 wraps (WorldSeed, OutreachFrame,
/// ClosedConversation, OutreachDecision, RecentOutreachContext,
/// EmotionalContext) unwrapped their envelope's <c>.Content</c>
/// immediately at the first consumer. That left the provenance fields
/// (<see cref="IProvenancedContent{T}.SourceType"/> /
/// <see cref="IProvenancedContent{T}.Producer"/> /
/// <see cref="IProvenancedContent{T}.CreatedAt"/>) unread in production.
/// This extension is the first read-side consumer — call
/// <see cref="LogProvenance{T}"/> once at each producer's envelope emit
/// site (before the unwrap), and every cycle's producer activity becomes
/// greppable via <c>F1_PROVENANCE</c> in the journal.
/// </para>
///
/// <para>
/// Structured-log fields (Serilog message template): <c>{SourceType}</c>,
/// <c>{Producer}</c>, <c>{CreatedAt}</c>. These are the exact interface
/// property names to keep the structured-log side searchable by them.
/// Log level is <c>LogInformation</c> so the F1_PROVENANCE lines land in
/// the default Info-tier journal (per ANI's dual-file Serilog: journal
/// Info+ / diagnostic Debug+). PR #124 review-fix (Devin): promoted from
/// Debug so <c>grep F1_PROVENANCE data/journal-*.log</c> works without
/// a separate serilog category override that could silently fail on
/// deploy. Volume is ~12 lines per cycle at most (one per envelope emit
/// site), trivial vs. existing Info-tier verbosity.
/// </para>
///
/// <para>
/// <b>Zero downstream impact:</b> no interface signature migrations, no
/// composer branching, no gate changes. Simply a shared observability
/// surface that all 14 F-1 producers can call uniformly.
/// </para>
/// </summary>
public static class ProvenancedContentLoggingExtensions
{
    /// <summary>
    /// Emit a structured <c>F1_PROVENANCE</c> log line for the given
    /// envelope. Safe to call from any producer's envelope-emit site.
    /// No-op on null envelope so opportunistic wiring is safe.
    /// </summary>
    /// <param name="envelope">
    /// The provenanced envelope to log. May be null (no-op).
    /// </param>
    /// <param name="log">
    /// The producer's ILogger. Log category is preserved from the caller
    /// so filters like <c>Serilog:MinimumLevel:Override:AniRuntime.LLM.ClosedConversationSummarizer</c>
    /// continue to work.
    /// </param>
    public static void LogProvenance<T>(
        this IProvenancedContent<T>? envelope, ILogger log)
    {
        if (envelope is null) return;

        log.LogInformation(
            "F1_PROVENANCE source={SourceType} producer={Producer} createdAt={CreatedAt:O}",
            envelope.SourceType, envelope.Producer, envelope.CreatedAt);
    }
}
