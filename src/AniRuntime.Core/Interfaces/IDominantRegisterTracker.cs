namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme R.1 (2026-05-24, Issue #64) — singleton tracker for Ani's most-recent
/// inner-thought dominant register. Updated by <c>CognitiveCyclePipeline</c>
/// after every inner-thought composition on the hybrid path (which emits
/// <c>Register</c> as part of the same generation). Read by <c>ContextBuilder</c>
/// to populate <c>ContextSnapshot.DominantRegister</c> for downstream consumers.
///
/// <para>
/// In-memory + ephemeral by design: the register is a per-cycle freshness
/// signal, lost on service restart. This is deliberate — the next cycle's
/// inner thought will populate it again within minutes.
/// </para>
///
/// <para>
/// Register values are drawn from <c>ClosedConversationSummarizer.Registers</c>
/// (the canonical 9-register taxonomy). Null when no inner thought has run
/// yet in this service instance.
/// </para>
/// </summary>
public interface IDominantRegisterTracker
{
    /// <summary>
    /// Most-recent register recorded, or null if none yet in this service
    /// instance. The returned value is the canonical register name (e.g.
    /// "Tenderness", "Frustration") — consumers should treat it as a string
    /// tag rather than parsing or normalizing.
    /// </summary>
    string? Current { get; }

    /// <summary>
    /// Record a new dominant register. Called by the cognitive cycle after
    /// inner-thought composition on the hybrid path when
    /// <c>innerResult.Register</c> is non-null. Caller responsibility:
    /// pass only canonical register names. Null or whitespace input is a
    /// no-op (defensive — caller may have a parse failure).
    /// </summary>
    void Record(string? register, DateTimeOffset at);
}
