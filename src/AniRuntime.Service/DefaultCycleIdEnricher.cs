using Serilog.Core;
using Serilog.Events;

namespace AniRuntime.Service;

/// <summary>
/// Foundation Observability (F-5) Phases 1 &amp; 2 (2026-08-24) — Serilog
/// enricher that ensures every log event carries the two correlation
/// properties (<c>CycleId</c> and <c>CyclePhase</c>) so the output template
/// can reference them unconditionally.
///
/// <para>
/// <b>Why this exists.</b> Mark's stated pain (2026-08-24) is that logs have
/// become impossible to parse — a single cognitive cycle spreads across
/// many phases, each writing its own log lines with its own prefix, and
/// there is no easy way to grep just the lines that belong to one cycle
/// (or to one phase inside that cycle). The fix is two correlation
/// identifiers attached via <see cref="Microsoft.Extensions.Logging.ILogger.BeginScope"/>:
/// <c>CycleId</c> pushed at <c>CognitiveCyclePipeline.RunAsync</c>, and
/// <c>CyclePhase</c> pushed at each phase entry point (perception, inner-thought,
/// conversation-reply, outreach, reactive-share, reflection).
///
/// <para>
/// <b>Why <c>CyclePhase</c> not <c>Phase</c>.</b> PR #146 Devin review-fix
/// (2026-08-24): pre-existing log statements use the message-template
/// placeholder <c>{Phase}</c> for unrelated domain concepts
/// (<c>EmotionalContextBuilder</c>'s relationship-phase enum,
/// <c>OutagePerceptionSource</c>'s FIRST/RE-EMIT event marker). Serilog's
/// scope enrichment uses <c>AddPropertyIfAbsent</c>, so the message-template
/// property wins and the pushed cognitive-phase scope would be silently
/// dropped on those lines. Naming the scope property <c>CyclePhase</c>
/// avoids the collision entirely without touching the pre-existing
/// log-message vocabularies.
/// </para> The Serilog
/// provider's built-in scope-to-property mapping (Serilog.Extensions.Logging)
/// unpacks the scope dictionary entries into log event properties, so every
/// log line emitted inside the scope carries both. This is a distinct
/// mechanism from <c>Enrich.FromLogContext</c> (which handles
/// <c>LogContext.PushProperty</c> calls, an AsyncLocal-scoped alternative
/// we don't use here — Devin PR #145 review-fix corrected an earlier
/// version of this comment that conflated the two mechanisms).
/// </para>
///
/// <para>
/// <b>Why the defaults are needed.</b> Serilog output templates that reference
/// a missing property render the placeholder literally
/// (e.g., <c>[cid:{CycleId}/{CyclePhase}]</c>) instead of eliding it. Non-cycle
/// log lines (Twilio webhook ingress, dashboard requests, background sweeps,
/// service startup) run outside any cycle scope; cycle-level lines outside
/// any phase scope (cycle start/end, cycle-scoped orchestration) run inside
/// a cycle but outside every phase. Both cases would produce ugly literal
/// placeholders without defaults. This enricher fills in a short marker
/// (<c>-</c>) for whichever field wasn't pushed, so ambient lines render
/// cleanly as <c>[cid:-/-]</c>, cycle-scoped lines as <c>[cid:abc12345/-]</c>,
/// and fully-scoped lines as <c>[cid:abc12345/InnerThought]</c>.
/// </para>
///
/// <para>
/// <b>Registration.</b> Wire this once in the Serilog configuration:
/// <c>.Enrich.With(new DefaultCycleIdEnricher())</c> AFTER
/// <c>.Enrich.FromLogContext()</c> so the pushed value wins when a cycle
/// scope is active and the default only fills the gap when it isn't.
/// </para>
/// </summary>
public sealed class DefaultCycleIdEnricher : ILogEventEnricher
{
    /// <summary>
    /// Marker rendered by log lines outside any cycle or phase scope. Short
    /// + visually distinct from any real cycle-id (which is 8-char hex
    /// derived from a Guid) or phase name, so
    /// <c>grep 'cid:-/-' ani-*.log</c> filters the ambient noise,
    /// <c>grep 'cid:abc12345' ani-*.log</c> pulls a specific cycle end-to-end,
    /// and <c>grep '/InnerThought]' ani-*.log</c> pulls every inner-thought
    /// log line across every cycle.
    /// </summary>
    public const string NoScopeMarker = "-";

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent is null) return;
        if (propertyFactory is null) return;

        if (!logEvent.Properties.ContainsKey("CycleId"))
        {
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty("CycleId", NoScopeMarker));
        }

        if (!logEvent.Properties.ContainsKey("CyclePhase"))
        {
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty("CyclePhase", NoScopeMarker));
        }
    }
}
