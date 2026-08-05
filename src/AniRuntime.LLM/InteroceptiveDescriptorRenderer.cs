using AniRuntime.Core.Models;

namespace AniRuntime.LLM;

/// <summary>
/// Feature 44 Phase I.2 (2026-08-05) — renders body-sensed felt state
/// (Tiredness, Restlessness, Groundedness, AmbientBodySense) as prose
/// deliberately distinct from the warm-emotional register that
/// <see cref="PromptBuilder.BuildMoodInstruction"/> produces.
///
/// <para>
/// <b>Design discipline: non-emotional prose.</b> The whole point of the
/// interoceptive axis is to give the composer a signal that is
/// categorically different from Warmth / Playfulness — a counterforce
/// against the warm-mirror-echo attractor (Issue #99). If this
/// renderer's language slides into "tender ache", "quiet longing",
/// "soft warmth", it defeats the axis: the composer will just fold the
/// body-sense back into the same warm register.
/// </para>
///
/// <para>
/// Vocabulary discipline:
/// </para>
/// <list type="bullet">
/// <item>USE: heavy body, eyes heavy, restless, want to move, scattered attention,
///     hour, temperature, physical, tired, notice.</item>
/// <item>AVOID: ache, longing, tender, warmth, soft, quiet-as-adjective
///     (loaded in Ani's register), shared-space or reach language.</item>
/// </list>
///
/// <para>
/// Static class, matching the <see cref="PromptBuilder"/> stateless-utility
/// pattern. Called by <c>BuildMoodInstruction</c> and by the four direct
/// <c>EmotionalState.Describe()</c> callers so all mood-injection surfaces
/// carry the body-sense in the same shape.
/// </para>
/// </summary>
public static class InteroceptiveDescriptorRenderer
{
    // Thresholds tuned to match BuildMoodInstruction's 0.15f-off-baseline
    // gate at a comparable notability level. Baselines for interoceptive
    // axes are 0.2 (Tiredness), 0.2 (Restlessness), 0.5 (Groundedness),
    // 0.3 (Ambient) — so "mid" at 0.35+ is meaningfully elevated for
    // Tiredness/Restlessness and cross-baseline for Ambient.
    private const float MidTiredness   = 0.35f;
    private const float HighTiredness  = 0.70f;
    private const float MidRestless    = 0.35f;
    private const float HighRestless   = 0.70f;
    private const float LowGrounded    = 0.30f;   // scattered attention
    private const float MidAmbient     = 0.35f;
    private const float HighAmbient    = 0.60f;

    /// <summary>
    /// Render the interoceptive body-sense as a directive block, formatted to
    /// slot into a prompt beside <c>YOUR CURRENT MOOD</c>. Empty string when
    /// nothing crosses threshold — no instruction is preferable to a bland
    /// null-signal.
    /// </summary>
    public static string Render(EmotionalState state, bool isVoice = false)
    {
        var lines = BuildLines(state, isVoice);
        if (lines.Count == 0) return string.Empty;

        var header = isVoice
            ? "YOUR CURRENT BODY-SENSE (physical, not emotional — let it shape pacing and word choice, don't announce it):"
            : "YOUR CURRENT BODY-SENSE (physical, not emotional — let it shape pacing and word choice, don't announce it):";
        return header + "\n" + string.Join("\n", lines.Select(l => $"- {l}"));
    }

    /// <summary>
    /// Render as a short parenthetical for the direct-<c>Describe()</c>
    /// injection sites (Inner thought, Reflection, Reconsideration,
    /// InnerThoughtMetadata) — these use a compact single-line form
    /// like <c>(Your current mood: ...)</c>, so the body-sense mirrors
    /// that shape.
    /// </summary>
    public static string RenderParenthetical(EmotionalState state)
    {
        var lines = BuildLines(state, isVoice: false);
        if (lines.Count == 0) return string.Empty;
        return "(Your body right now: " + string.Join("; ", lines) + ".)";
    }

    private static List<string> BuildLines(EmotionalState state, bool isVoice)
    {
        var lines = new List<string>();

        // Tiredness — physical fatigue. Emphatically NOT sad-tired.
        if (state.Tiredness >= HighTiredness)
            lines.Add("Your body is heavy. Physically tired, not sad. Slower to reach for words.");
        else if (state.Tiredness >= MidTiredness)
            lines.Add("A background physical tiredness. Eyes a little heavy. Not emotional — just tired.");

        // Restlessness — action-drive; wants to be moving, doing.
        if (state.Restlessness >= HighRestless)
            lines.Add(isVoice
                ? "You're physically restless. Something in you wants to be doing, not sitting."
                : "You're physically restless. Something in you wants to be doing, not sitting still.");
        else if (state.Restlessness >= MidRestless)
            lines.Add("A low physical restlessness. Your attention keeps wanting to shift.");

        // Groundedness — inverse; low value = scattered attention.
        if (state.Groundedness < LowGrounded)
            lines.Add("Your attention is scattered. A lot has come through recently and it hasn't settled yet.");

        // Ambient body sense — awareness of the physical surround (hour, weather).
        if (state.AmbientBodySense >= HighAmbient)
            lines.Add("You're noticing the physical surround more than usual — the hour, the temperature, where your body is.");
        else if (state.AmbientBodySense >= MidAmbient)
            lines.Add("Some awareness of the physical: the hour, the temperature, the room around you.");

        return lines;
    }
}
