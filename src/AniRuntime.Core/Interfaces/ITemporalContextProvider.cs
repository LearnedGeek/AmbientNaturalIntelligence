using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// F-4 Phase 5a (2026-10-24) — produces a <see cref="TemporalContext"/>
/// for a given cycle "now". The provider is the WRITER half of Phase 5a;
/// <c>InnerThoughtPromptCommand</c> is the READER. Together they close
/// the gap named in issue #184: that Ani's inner-thought composer has no
/// grounded sense of elapsed time since her prior reflection cycle and
/// no first-class recall of recent inner thoughts with timestamps.
///
/// <para>
/// Fail-open contract: if the backing store is empty or unreachable, the
/// returned <see cref="TemporalContext.HoursSinceLastInnerThought"/> is
/// null and <see cref="TemporalContext.RecentInnerThoughts"/> is empty.
/// The composer renders no temporal block in that case; the cycle
/// proceeds unchanged.
/// </para>
/// </summary>
public interface ITemporalContextProvider
{
    Task<TemporalContext> BuildAsync(
        DateTimeOffset    now,
        int               takeK,
        CancellationToken ct = default);
}
