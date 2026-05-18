using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Feature 3 — records a silence-choice narrative when desire was notable
/// but Ani held back from reaching out. Rate-limited to one record per
/// 4-hour window (cooldown state lives on the implementation, which is
/// therefore Singleton).
///
/// <para>
/// The narrative is saved as an Interior-tier InnerThought memory so it
/// shows up in later cycles' self-model substrate ("I almost reached out
/// but…"). Extracted from <c>OutreachPhase</c> in §5.3 SOLID refactor.
/// </para>
/// </summary>
public interface ISilenceChoiceRecorder
{
    Task RecordAsync(DesireState desireState, EmotionalState emotionalState, CancellationToken ct);
}
