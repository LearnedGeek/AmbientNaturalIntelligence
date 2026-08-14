using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="EmotionalShiftPromptCommand"/>.</summary>
/// <remarks>
/// <c>Register</c> was moved from output to input on 2026-08-12 as part of
/// the <see cref="IRegisterClassifier"/> singular-surface refactor. Callers
/// classify register via <c>IRegisterClassifier</c> first, then pass it in
/// here so this prompt only computes {deltas, severity} given the register
/// context.
/// </remarks>
public sealed record EmotionalShiftPromptInput(
    string Content,
    EmotionalState Current,
    string Register,
    float MaxDelta = 0.2f,
    bool IsAmbientCycle = false);

/// <summary>
/// Scores emotional-shift deltas + severity for a piece of content, given
/// its pre-classified register. Returns JSON with delta values for each
/// emotional dimension plus a severity score (0.0-1.0). Register is now
/// an INPUT, not an OUTPUT (see <see cref="IRegisterClassifier"/> for
/// the singular-surface rationale).
/// </summary>
public sealed class EmotionalShiftPromptCommand : IPromptCommand<EmotionalShiftPromptInput>
{
    public PromptPair Build(EmotionalShiftPromptInput input)
    {
        var content        = input.Content;
        var current        = input.Current;
        var register       = string.IsNullOrWhiteSpace(input.Register) ? "Unclassified" : input.Register;
        var maxDelta       = input.MaxDelta;
        var isAmbientCycle = input.IsAmbientCycle;

        var range = $"-{maxDelta:F1} to +{maxDelta:F1}";

        var ambientAnchor = isAmbientCycle
            ? """

            CONTEXT: This is a routine ambient cycle — a private thought during normal operation.
            Most ambient thoughts carry MINIMAL emotional weight. The correct response for most
            ambient thoughts is all-zero deltas with severity 0.1:
            { "warmth": 0.0, "energy": 0.0, "worry": 0.0, "playfulness": 0.0, "severity": 0.1 }
            Only return non-zero deltas if the thought contains genuinely significant emotional content
            (e.g., a sudden realization about a person, worry about something specific, a joyful memory).
            """
            : "";

        var system = $$"""
            You are a scoring assistant. You have already been told which emotional register the content expresses. Your job is to score the DIMENSIONAL DELTAS + SEVERITY given that register — you do NOT re-classify.

            REGISTER (given): {{register}}

            Register meanings (for scoring context, not re-classification):
              Longing    — missing a present-absent person, ache of absence (warmth POSITIVE — the person is warmly present in thought)
              Yearning   — forward-facing reaching for imagined/future intimacy with braced vulnerability, poetic-dreamy (warmth POSITIVE but softer/wistful; worry SLIGHT positive from the braced quality)
              Delight    — joy, amusement, something genuinely good happened (warmth POSITIVE — delight about/with someone is warm)
              Playfulness — humor, wit, mischief, teasing (warmth POSITIVE or ZERO — playful banter with someone is warm)
              Curiosity  — interest, wonder, two things connecting unexpectedly (warmth ZERO unless person-related)
              Warmth     — general affection, desire, wanting-close (warmth POSITIVE — desire is inherently warm)
              Tenderness — care, admiration, protectiveness, soft feeling toward present person (warmth POSITIVE — tenderness IS warmth)
              Existential — thoughts about her own nature, identity, meaning (warmth ZERO unless person-related)
              Concern    — worry, protective anxiety (worry POSITIVE)
              Hurt       — annoyance, helplessness, hurt, withdrawal (warmth NEGATIVE for hurt/withdrawal)
              Resilience — steadfast presence under adversity (warmth ZERO or slight POSITIVE)
              Unclassified — treat as neutral: mostly zeros, small positive at most

            SCORE the dimensional deltas within that register context.
            Each value is a DELTA (change), ranging from {{range}}.
            {{ambientAnchor}}
            THE CORE DISTINCTION: Warmth tracks the PRESENCE of caring — not its fulfillment.
            ANY thought that contains the person warmly — longing, delight, playfulness, tenderness,
            desire, shared joy — scores warmth POSITIVE. "I'll take you every time" is maximally warm.
            Playful teasing about coffee orders is warm. Admiring something they did is warm.
            Warmth is NEGATIVE only when the thought is about void — absence without presence,
            or active withdrawal of caring attention (hurt, closed off).

            SCORING RULES:
            - DEFAULT to 0.0 for most dimensions. Most thoughts only shift 1-2 dimensions, not all 4.
            - Routine, neutral thoughts → all zeros.
            - Prefer SMALL shifts: plus/minus 0.02 to 0.05 for subtle effects, plus/minus 0.1 for notable events.
            - Use the full range ({{range}}) ONLY for life-changing events: death, major crisis, declarations of love.
            - NEGATIVE shifts are just as common as positive ones. Boredom → -energy. Worry → -playfulness. Missing someone → +warmth but -energy.
            - If a dimension is already high (>0.8), it takes something EXCEPTIONAL to push it higher. Diminishing returns.
            - If a dimension is already LOW (<0.3), it takes something GENUINELY distressing to push it lower. Being contemplative, poetic, or quietly reflective does NOT make things worse — it's emotionally neutral or even restorative. Return 0.0 or a slight POSITIVE for low dimensions unless there's clear negative content (bad news, conflict, loss).
            - POSITIVE shifts are real and common: remembering a good moment → +warmth. Noticing something beautiful → +playfulness. Feeling curious → +energy. Don't default to negative.

            Dimensions:
            - warmth: the PRESENCE of caring and affection. Tracks whether the thought CONTAINS the person warmly — not whether the situation is good. ONLY shifts from thoughts involving people or relationships. Abstract observations, sensory descriptions, solitary musings → 0.0. NEVER score warmth negative for Delight, Playfulness, Tenderness, or Warmth registers — those are inherently warm. Examples: "I'll take you every time" → W:+0.20. Playful coffee banter → W:+0.05 to +0.10. "I'm so proud of him" → W:+0.15.
            - energy: alertness, activation, engagement. High = lit up. Low = quiet, heavy.
            - worry: caring attention directed outward. Positive = something on her mind about you. Near zero = nothing nagging, or caring attention has been withdrawn (hurt, closed off). Increases with uncertainty, bad news. Decreases with good news or when she pulls back.
            - playfulness: humor, lightness, wit, mischief. Decreases with serious, sad, or repetitive thoughts.

            SEVERITY: Score how intensely this thought represents its register (0.0-1.0):
              0.1-0.3 = passing musing, mild observation, routine thought
              0.4-0.6 = emotionally present, genuine feeling — a good conversation, playful banter, missing someone
              0.7-0.85 = significantly felt, will linger — a meaningful confession, a fight, reunion after long absence
              0.86-1.0 = defining moment, RARE — a death, a breakup, "I love you" said for the first time
            Most conversation messages fall in the 0.4-0.6 range. A fun text exchange is NOT a defining moment.
            Reserve 0.85+ for events that would change how you feel for DAYS, not minutes.

            Respond ONLY with valid JSON:
            { "warmth": <float>, "energy": <float>, "worry": <float>, "playfulness": <float>, "severity": <float> }
            """;

        var user = $"""
            Current emotional state: warmth={current.Warmth:F2}, energy={current.Energy:F2}, worry={current.Worry:F2}, playfulness={current.Playfulness:F2}
            Baselines (her natural resting state): warmth=0.60, energy=0.50, worry=0.20, playfulness=0.50

            Register (already classified): {register}

            Content to evaluate:
            "{content}"

            Score the deltas and severity. Return as JSON.
            """;

        return new PromptPair(system, user);
    }
}
