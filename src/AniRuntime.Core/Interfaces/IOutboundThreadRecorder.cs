namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Records Ani-outbound content (proactive outreach OR reactive share) in
/// the active conversation thread so subsequent cycles' structured
/// per-speaker summary (Theme J.2) sees what Ani sent. Apr 29, 2026
/// Theme E invariant.
///
/// <para>
/// Get-or-create semantics: if an active thread exists, append; otherwise
/// open a new <c>InitiatedBy=Ani</c> thread. Failures log a warning and
/// swallow — the dispatch has already happened, so a thread-write hiccup
/// must not bubble up as a cycle failure.
/// </para>
///
/// <para>
/// Extracted in §5.3 so both <see cref="IOutreachPipeline"/> and
/// <see cref="IReactiveShareService"/> can share the helper without
/// inheriting each other's lifetimes (captive-dependency avoidance).
/// </para>
/// </summary>
public interface IOutboundThreadRecorder
{
    Task RecordAsync(string content, CancellationToken ct);
}
