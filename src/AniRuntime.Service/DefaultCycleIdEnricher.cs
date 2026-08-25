using Serilog.Core;
using Serilog.Events;

namespace AniRuntime.Service;

/// <summary>
/// Foundation Observability (F-5) Phase 1 (2026-08-24) — Serilog enricher
/// that ensures every log event carries a <c>CycleId</c> property so the
/// output template can reference it unconditionally.
///
/// <para>
/// <b>Why this exists.</b> Mark's stated pain (2026-08-24) is that logs have
/// become impossible to parse — a single cognitive cycle spreads across
/// many phases, each writing its own log lines with its own prefix, and
/// there is no easy way to grep just the lines that belong to one cycle.
/// The fix is a cycle-scoped correlation identifier attached via
/// <see cref="Microsoft.Extensions.Logging.ILogger.BeginScope"/> at the top
/// of <c>CognitiveCyclePipeline.RunAsync</c>; every log line emitted inside
/// that scope automatically picks up the ID via the
/// <c>Enrich.FromLogContext</c> enricher already wired in Program.cs.
/// </para>
///
/// <para>
/// <b>Why the default is needed.</b> Serilog output templates that reference
/// a missing property render the placeholder literally
/// (e.g., <c>[cid:{CycleId}]</c>) instead of eliding it. Non-cycle log
/// lines (Twilio webhook ingress, dashboard requests, background sweeps,
/// service startup) run outside any cycle scope and would produce that
/// ugly literal placeholder. This enricher fills in a short marker
/// (<c>-</c>) when no cycle scope pushed a real value, so non-cycle lines
/// render cleanly as <c>[cid:-]</c> — visually distinct from real cycle IDs
/// but never a broken placeholder.
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
    /// Marker rendered by non-cycle log lines. Short + visually distinct
    /// from any real cycle-id (which is 8-char hex derived from a Guid),
    /// so <c>grep 'cid:-' ani-*.log</c> filters the ambient noise and
    /// <c>grep 'cid:abc12345' ani-*.log</c> pulls a specific cycle end-to-end.
    /// </summary>
    public const string NoCycleMarker = "-";

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent is null) return;
        if (propertyFactory is null) return;

        if (!logEvent.Properties.ContainsKey("CycleId"))
        {
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty("CycleId", NoCycleMarker));
        }
    }
}
