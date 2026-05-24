using AniRuntime.Core.Models;

namespace AniRuntime.Core.Utilities;

/// <summary>
/// Theme R.4 (2026-05-24, Issue #64) — maps a Layer 2 <see cref="MotivationVector"/>
/// to a dominant emphasis key for composer-side structural branching.
/// Mirror of <see cref="RegisterPromptVariant"/>'s shape — small variant set,
/// deterministic mapping, used by composers to pick code-path branches.
///
/// <para>Threshold rule: an axis is "dominant" when it exceeds the others by
/// a clear margin (<see cref="DominanceMargin"/>). When no axis dominates,
/// the vector is <see cref="Balanced"/>.</para>
/// </summary>
public static class MotivationEmphasis
{
    public const string Relatedness = "relatedness";
    public const string Autonomy    = "autonomy";
    public const string Competence  = "competence";
    public const string Balanced    = "balanced";

    /// <summary>
    /// Margin by which an axis must exceed the others to be considered
    /// dominant. Conservative; the typical Relatedness range [0.3, 1.5]
    /// means random vectors won't tip into a dominance state without
    /// genuine signal.
    /// </summary>
    public const float DominanceMargin = 0.15f;

    public static string Select(MotivationVector? vector)
    {
        if (vector is not { } v) return Balanced;

        var r = v.Relatedness;
        var a = v.Autonomy;
        var c = v.Competence;

        if (r >= a + DominanceMargin && r >= c + DominanceMargin) return Relatedness;
        if (a >= r + DominanceMargin && a >= c + DominanceMargin) return Autonomy;
        if (c >= r + DominanceMargin && c >= a + DominanceMargin) return Competence;
        return Balanced;
    }
}
