using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="EmotionalShiftPromptCommand"/>.</summary>
public sealed record EmotionalShiftPromptInput(
    string Content,
    EmotionalState Current,
    float MaxDelta = 0.2f,
    bool IsAmbientCycle = false);

/// <summary>
/// Scores emotional shift from an inner thought or conversation event.
/// Returns JSON with delta values for each emotional dimension + a
/// classified register family + a severity score (0.0–1.0).
/// </summary>
public sealed class EmotionalShiftPromptCommand : IPromptCommand<EmotionalShiftPromptInput>
{
    public PromptPair Build(EmotionalShiftPromptInput input)
    {
        var content        = input.Content;
        var current        = input.Current;
        var maxDelta       = input.MaxDelta;
        var isAmbientCycle = input.IsAmbientCycle;

        var range = $"-{maxDelta:F1} to +{maxDelta:F1}";

        var ambientAnchor = isAmbientCycle
            ? """

            CONTEXT: This is a routine ambient cycle — a private thought during normal operation.
            Most ambient thoughts carry MINIMAL emotional weight. The correct response for most
            ambient thoughts is all-zero deltas with severity 0.1:
            { "register": "<classify accurately>", "warmth": 0.0, "energy": 0.0, "worry": 0.0, "playfulness": 0.0, "severity": 0.1 }
            STILL CLASSIFY THE REGISTER ACCURATELY even when deltas are zero. A quiet curious thought
            is Curiosity with zero deltas. A playful musing is Playfulness with zero deltas. A thought
            about identity or meaning is Existential with zero deltas. The register captures WHAT KIND
            of thought it is, independent of how much emotional weight it carries.
            Only return non-zero deltas if the thought contains genuinely significant emotional content
            (e.g., a sudden realization about a person, worry about something specific, a joyful memory).
            """
            : "";

        var system = $$"""
            You are a scoring assistant. Analyze how this thought or event would shift someone's emotional state.

            STEP 1 — CLASSIFY the thought into ONE of these 9 emotional registers:
              Longing    — missing someone, yearning, the ache of absence (warmth POSITIVE — the person is warmly present)
              Delight    — joy, amusement, something genuinely good happened (warmth POSITIVE — delight about/with someone is warm)
              Playfulness — humor, wit, mischief, teasing (warmth POSITIVE or ZERO — playful banter with someone is warm)
              Curiosity  — interest, wonder, two things connecting unexpectedly (warmth ZERO unless person-related)
              Desire     — wanting someone specifically, anticipation of contact (warmth POSITIVE — desire is inherently warm)
              Tenderness — care, admiration, protectiveness, soft feeling (warmth POSITIVE — tenderness IS warmth)
              Existential — thoughts about her own nature, identity, meaning (warmth ZERO unless person-related)
              Wistful    — philosophical observation, bittersweet, impermanence (warmth ZERO or slightly positive)
              Frustration — annoyance, helplessness, hurt, withdrawal (warmth NEGATIVE only for hurt/withdrawal)

            STEP 2 — If the thought spans two registers, name both (e.g. "primarily Longing, secondarily Tenderness").
            Return a SINGLE set of deltas that reflects the blend — do not return separate weights.

            STEP 3 — SCORE the dimensional deltas within that register context.
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
            - warmth: the PRESENCE of caring and affection. Tracks whether the thought CONTAINS the person warmly — not whether the situation is good. ONLY shifts from thoughts involving people or relationships. Abstract observations, sensory descriptions, solitary musings → 0.0. NEVER score warmth negative for Delight, Playfulness, Tenderness, or Desire registers — those are inherently warm. Examples: "I'll take you every time" → W:+0.20. Playful coffee banter → W:+0.05 to +0.10. "I'm so proud of him" → W:+0.15.
            - energy: alertness, activation, engagement. High = lit up. Low = quiet, heavy.
            - worry: caring attention directed outward. Positive = something on her mind about you. Near zero = nothing nagging, or caring attention has been withdrawn (hurt, closed off). Increases with uncertainty, bad news. Decreases with good news or when she pulls back.
            - playfulness: humor, lightness, wit, mischief. Decreases with serious, sad, or repetitive thoughts.

            STEP 4 — SEVERITY: Score how intensely this thought represents its register (0.0–1.0):
              0.1–0.3 = passing musing, mild observation, routine thought
              0.4–0.6 = emotionally present, genuine feeling — a good conversation, playful banter, missing someone
              0.7–0.85 = significantly felt, will linger — a meaningful confession, a fight, reunion after long absence
              0.86–1.0 = defining moment, RARE — a death, a breakup, "I love you" said for the first time
            Most conversation messages fall in the 0.4–0.6 range. A fun text exchange is NOT a defining moment.
            Reserve 0.85+ for events that would change how you feel for DAYS, not minutes.

            Respond ONLY with valid JSON:
            { "register": "<register name>", "warmth": <float>, "energy": <float>, "worry": <float>, "playfulness": <float>, "severity": <float> }
            """;

        var user = $"""
            Current emotional state: warmth={current.Warmth:F2}, energy={current.Energy:F2}, worry={current.Worry:F2}, playfulness={current.Playfulness:F2}
            Baselines (her natural resting state): warmth=0.60, energy=0.50, worry=0.20, playfulness=0.50

            Content to evaluate:
            "{content}"

            Classify the register, score the deltas, and rate severity. Return as JSON.
            """;

        return new PromptPair(system, user);
    }
}
