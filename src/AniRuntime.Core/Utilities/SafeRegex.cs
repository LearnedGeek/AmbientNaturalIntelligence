using System.Text.RegularExpressions;

namespace AniRuntime.Core.Utilities;

/// <summary>
/// Timeout-wrapped Regex façade. The .NET <see cref="Regex"/> static helpers
/// without an explicit <c>matchTimeout</c> argument are flagged by SonarCloud
/// rule <c>csharpsquid:S6444</c> as catastrophic-backtracking DoS surfaces.
/// Most of ANI's regex use is on substrate content (user-supplied or model-
/// generated text), so the surface is real even if exploitation is unlikely.
///
/// Threading this fix through the codebase via per-call <c>TimeSpan</c>
/// arguments would touch every regex site. This helper centralises the
/// timeout, so call sites read identically to the framework API but inherit
/// a safe default.
///
/// <para>
/// **Timeout choice (2026-05-18):** 2 seconds. ANI's regex use is in the
/// hot path of cognitive cycles and conversation handling; a 2s ceiling is
/// roughly 200× the expected normal-case runtime of any pattern in the
/// codebase and short enough that runaway matches surface as
/// <see cref="RegexMatchTimeoutException"/> well before they can DoS a cycle.
/// </para>
/// </summary>
public static class SafeRegex
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    // ── IsMatch ────────────────────────────────────────────────────────
    public static bool IsMatch(string input, string pattern)
        => Regex.IsMatch(input, pattern, RegexOptions.None, DefaultTimeout);

    public static bool IsMatch(string input, string pattern, RegexOptions options)
        => Regex.IsMatch(input, pattern, options, DefaultTimeout);

    // ── Match ──────────────────────────────────────────────────────────
    public static Match Match(string input, string pattern)
        => Regex.Match(input, pattern, RegexOptions.None, DefaultTimeout);

    public static Match Match(string input, string pattern, RegexOptions options)
        => Regex.Match(input, pattern, options, DefaultTimeout);

    // ── Matches ────────────────────────────────────────────────────────
    public static MatchCollection Matches(string input, string pattern)
        => Regex.Matches(input, pattern, RegexOptions.None, DefaultTimeout);

    public static MatchCollection Matches(string input, string pattern, RegexOptions options)
        => Regex.Matches(input, pattern, options, DefaultTimeout);

    // ── Replace ────────────────────────────────────────────────────────
    public static string Replace(string input, string pattern, string replacement)
        => Regex.Replace(input, pattern, replacement, RegexOptions.None, DefaultTimeout);

    public static string Replace(string input, string pattern, string replacement, RegexOptions options)
        => Regex.Replace(input, pattern, replacement, options, DefaultTimeout);

    public static string Replace(string input, string pattern, MatchEvaluator evaluator)
        => Regex.Replace(input, pattern, evaluator, RegexOptions.None, DefaultTimeout);

    public static string Replace(string input, string pattern, MatchEvaluator evaluator, RegexOptions options)
        => Regex.Replace(input, pattern, evaluator, options, DefaultTimeout);

    // ── Split ──────────────────────────────────────────────────────────
    public static string[] Split(string input, string pattern)
        => Regex.Split(input, pattern, RegexOptions.None, DefaultTimeout);

    public static string[] Split(string input, string pattern, RegexOptions options)
        => Regex.Split(input, pattern, options, DefaultTimeout);

    // ── Instance factory ───────────────────────────────────────────────
    /// <summary>
    /// Constructs a <see cref="Regex"/> with the project's default timeout.
    /// Prefer this over <c>new Regex(...)</c> at call sites; the underlying
    /// instance behaves identically except the match timeout is enforced.
    /// </summary>
    public static Regex Create(string pattern)
        => new Regex(pattern, RegexOptions.None, DefaultTimeout);

    public static Regex Create(string pattern, RegexOptions options)
        => new Regex(pattern, options, DefaultTimeout);
}
