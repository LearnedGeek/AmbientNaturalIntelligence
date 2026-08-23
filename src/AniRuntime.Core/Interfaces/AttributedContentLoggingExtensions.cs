using Microsoft.Extensions.Logging;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P8 (2026-08-23) — the first
/// read-side consumer of <see cref="IAttributedContent{T}"/> attribution
/// fields. Emits a structured Serilog log line at every producer emit
/// site so attribution decisions are observable in journal logs without
/// changing any downstream signature.
///
/// <para>
/// P6 wired attribution at nine producer sites (CognitiveCyclePipeline
/// inner thought, OutreachPipeline outreach, ReactiveShareService,
/// SilenceChoiceRecorder, PerceptionPhase, PostReplyEmotionalProcessor,
/// ReflectionPhase, EfReflectionGistService, MemoryWriteAction,
/// SqliteConversationService). Each populates an
/// <see cref="AniRuntime.Core.Models.AttributionTriple"/> at write time.
/// This extension is the observability surface — call
/// <see cref="LogAttribution{T}"/> once at each site alongside the save
/// so every cycle's attribution activity becomes greppable via
/// <c>F2_ATTRIBUTION</c> in the journal.
/// </para>
///
/// <para>
/// Structured-log fields (Serilog message template): <c>{AttributedTo}</c>,
/// <c>{Trust}</c>, <c>{AttributedAt}</c>, <c>{SourceDescriptor}</c>.
/// SourceRecordId is omitted (populated only by
/// <see cref="AniRuntime.Core.Models.AttributionTriple.AniFromRecord"/>).
/// Log level is <c>LogInformation</c> so the F2_ATTRIBUTION lines land
/// in the default Info-tier journal (per ANI's dual-file Serilog: journal
/// Info+ / diagnostic Debug+). Volume is ~5-15 lines per cycle at most,
/// trivial vs. existing Info-tier verbosity.
/// </para>
///
/// <para>
/// Companion to <see cref="ProvenancedContentLoggingExtensions.LogProvenance{T}"/>:
/// F1_PROVENANCE captures the producer-boundary envelope shape; F2_ATTRIBUTION
/// captures the who + trust of the persisted record. Both extensions bind
/// through their respective interfaces without per-shape overloads.
/// </para>
/// </summary>
public static class AttributedContentLoggingExtensions
{
    /// <summary>
    /// Emit a structured <c>F2_ATTRIBUTION</c> log line for the given
    /// attributed content. Safe to call from any producer save site.
    /// No-op on null content so opportunistic wiring is safe.
    /// </summary>
    /// <param name="attributed">
    /// The attributed content to log. May be null (no-op).
    /// </param>
    /// <param name="log">
    /// The producer's ILogger. Log category is preserved from the caller
    /// so per-category serilog overrides continue to work.
    /// </param>
    public static void LogAttribution<T>(
        this IAttributedContent<T>? attributed, ILogger log)
    {
        if (attributed is null) return;

        log.LogInformation(
            "F2_ATTRIBUTION attributedTo={AttributedTo} trust={Trust} attributedAt={AttributedAt:O} descriptor={SourceDescriptor}",
            attributed.AttributedTo,
            attributed.AttributionTrust,
            attributed.AttributedAt,
            attributed.AttributedSourceDescriptor);
    }

    /// <summary>
    /// Convenience overload for <see cref="AniRuntime.Core.Models.MemoryRecord"/>,
    /// the only concrete <see cref="IAttributedContent{T}"/> implementer today.
    /// Keeps producer call sites free of the interface cast that explicit-interface
    /// implementation would otherwise require.
    /// </summary>
    public static void LogAttribution(this AniRuntime.Core.Models.MemoryRecord? record, ILogger log)
    {
        if (record is null) return;
        ((IAttributedContent<AniRuntime.Core.Models.MemoryRecord>)record).LogAttribution(log);
    }
}
