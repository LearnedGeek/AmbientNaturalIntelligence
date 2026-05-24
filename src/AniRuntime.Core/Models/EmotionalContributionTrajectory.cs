namespace AniRuntime.Core.Models;

/// <summary>
/// Theme R.6 (2026-05-24, Issue #64) — compact summary of the per-thought
/// decayed-contribution trajectory across a recent window. Composers consume
/// this STRUCTURALLY (branch on volatility level + dominant-register list)
/// rather than reading the raw <c>List&lt;EmotionalContribution&gt;</c>.
///
/// <para>
/// Volatility is a 0-1 normalized indicator of how much state has shifted
/// across the recent contributions window. Low volatility means stable;
/// high means recent fluctuation. Threshold for "high" is composer-side
/// (currently 0.4 — chosen empirically against the per-cycle dynamic range
/// of WarmthDelta).
/// </para>
///
/// <para>
/// <see cref="DominantRegistersRecent"/> lists the most-frequent registers
/// over the same window, ordered by count (most-frequent first). Useful for
/// composers to see whether the recent emotional thread has been consistent
/// (one register) or polyphonic (several).
/// </para>
/// </summary>
public sealed record EmotionalContributionTrajectory(
    float RecentVolatility,
    IReadOnlyList<string> DominantRegistersRecent);
