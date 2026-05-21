namespace AniRuntime.Core.Models;

/// <summary>
/// Output of <c>IStateContextBuilder.BuildAsync</c>. Carries the six core
/// state-pull fields that <c>ContextBuilder</c> previously assembled inline,
/// plus the derived outreach-continuity context and thought-diversity nudge.
/// Extracted 2026-05-19 as the fifth and final sub-builder of the
/// ContextBuilder SRP decomposition (Testability Architecture Plan §2).
/// </summary>
/// <remarks>
/// <c>EmotionalState</c> is the loaded state when the orchestrator did not
/// supply an override; otherwise the override is round-tripped through to
/// match the pre-extraction inline behavior where the orchestrator could
/// override the state from external (e.g., post-shift emotional value).
/// </remarks>
public sealed record StateContextResult(
    CharacterStateDoc           CharacterState,
    DesireState                 DesireState,
    EmotionalState              EmotionalState,
    List<OpenLoop>              OpenLoops,
    RecentOutreachContext       OutreachContext,
    string?                     ThoughtDiversityNudge);
