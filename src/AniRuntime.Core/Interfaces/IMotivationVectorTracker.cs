using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme R.4 (2026-05-24, Issue #64) — singleton tracker for the most-recent
/// Layer 2 motivation vector. Updated by <c>CognitiveCyclePipeline</c> after
/// every <c>MotivationScorer.ScoreVector</c> call. Read by <c>ContextBuilder</c>
/// to populate <c>ContextSnapshot.MotivationVector</c> for downstream consumers.
///
/// <para>
/// Closes the "computed-then-logged-never-piped-back" gap surfaced by the
/// 2026-05-24 data-flow audit — Layer 2 Phase 2a built the vector but no
/// consumer was wired. R.4 makes it a structural composer input.
/// </para>
///
/// <para>
/// In-memory + ephemeral by design (same shape as
/// <see cref="IDominantRegisterTracker"/>). Lost on service restart; next
/// cycle repopulates within minutes.
/// </para>
/// </summary>
public interface IMotivationVectorTracker
{
    /// <summary>
    /// Most-recent motivation vector recorded, or null if none yet in this
    /// service instance.
    /// </summary>
    MotivationVector? Current { get; }

    /// <summary>
    /// Record a new motivation vector. Called from the cognitive cycle
    /// immediately after <c>MotivationScorer.ScoreVector</c>.
    /// </summary>
    void Record(MotivationVector vector, DateTimeOffset at);
}
