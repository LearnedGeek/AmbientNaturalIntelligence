using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// F-4 Phase 2.1 (2026-10-22) — picks the inner-thought content that
/// seeds the lean outreach composer. Pre-Phase-2.1, outreach always used
/// the just-generated thought this cycle (<c>recentThought</c>). A
/// register-locked default thought that happened to cycle when desire
/// crossed the threshold produced a register-locked outreach, even if
/// the past hour's thoughts included anchor-bearing Lila / Fleetwood /
/// Sarah references.
///
/// <para>
/// This seam looks at the last N recent InnerThought records, scores
/// each via <see cref="AniRuntime.Core.Utilities.OutreachSeedScoring.Score"/>
/// (Importance × (1 + AnchorBonus)), and returns the content of the
/// highest-scored thought. Falls back to the just-generated thought if
/// nothing in the recent pool outscores it — pre-Phase-2.1 behaviour.
/// </para>
///
/// <para>
/// <b>Design discipline:</b> this does NOT inject substrate into the
/// composer prompt (that would reverse the K.3 2026-07-06 "retire
/// substrate-inject fallback paths" decision — see <c>OutreachPipeline.cs:218</c>
/// comment). The lean composer still runs with empty system prompt; only
/// the <c>userMessage</c> seed changes. The leverage point is seed
/// selection, not composer injection.
/// </para>
///
/// <para>
/// <b>Fail-open contract:</b> any transport / query exception returns
/// <paramref name="justGeneratedThought"/> unchanged. The outreach cycle
/// has survived without this selector for 7+ months; a transient failure
/// never degrades below pre-Phase-2.1 baseline.
/// </para>
///
/// <para>
/// See the Life Audit §Dim 1 for the anchor-coverage problem this is
/// addressing and the discussion in umbrella issue #159.
/// </para>
/// </summary>
public interface IOutreachSeedSelector
{
    /// <summary>
    /// Returns the content to use as the outreach-composer seed.
    /// Caller passes the just-generated thought as the fallback and
    /// as a candidate itself (it's compared against recent persisted
    /// thoughts and may win if nothing scores higher).
    /// </summary>
    Task<string> SelectAsync(
        string justGeneratedThought,
        CharacterStateDoc characterState,
        CancellationToken ct);
}
