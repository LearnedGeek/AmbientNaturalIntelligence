using System.Text.RegularExpressions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Door B Truth-Verification, Sub-claim 2 (May 2, 2026) — day-of-week
/// and calendar verification. Pure regex + clock comparison; no LLM
/// call. Catches the May 2 11:58 *"hope your evening went smooth - how
/// was work?"* class only when the day-of-week is directly asserted
/// (Sub-claim 3 handles the state-now / "how was work" case via
/// perceptions; this sub-claim handles only direct day-of-week claims).
///
/// **Patterns caught:**
/// 1. Direct present-tense day claim: *"today is Sunday"* on a non-Sunday
/// 2. Greeting-form day claim: *"happy Saturday!"* on a non-Saturday
/// 3. *"this [day]"* with present-tense markers: *"this Sunday already
///    feels warmer"*, *"this Saturday is..."*, *"this Sunday seems..."*
///
/// **Patterns NOT caught (deliberately):**
/// - Generic plural references: *"Sundays are warmer"* — generic, not a
///   today-claim. Sub-claim 1 (semantic search over Mark turns) catches
///   the originating claim.
/// - Forward-looking: *"this Saturday will be..."* — implies upcoming
///   Saturday next week.
/// - Past-tense: *"I worked on Monday"* — historical reference.
/// - *"this weekend"* — ambiguous direction across Thu-Mon window.
///
/// **Type-conditional applicability:** Dispatch sinks for
/// ConversationReply / Outreach / Voice. Same set as Door C.
///
/// **Day-of-week comparison uses local time** because day-of-week is
/// inherently local. <c>artifact.GeneratedAt.ToLocalTime()</c> reads the
/// process's local TZ — server runs in Mark's timezone (Central US), so
/// this aligns naturally. If timezone diverges in future deployments,
/// inject a clock with TZ awareness.
/// </summary>
public sealed class TemporalAnchorInvariant : ICognitiveOutputInvariant
{
    private readonly ILogger<TemporalAnchorInvariant> _log;
    private readonly bool _enabled;

    public string Name => "temporal-anchor";

    /// <summary>
    /// Production constructor — reads <see cref="AniOptions.TemporalHeuristicInvariantsEnabled"/>
    /// via DI. When the flag is off (default per Step 2b), this invariant
    /// is no-op in the pipeline; the cloud verifier's q4 check handles
    /// temporal contradictions with substrate awareness.
    /// </summary>
    public TemporalAnchorInvariant(ILogger<TemporalAnchorInvariant> log, IOptions<AniOptions> options)
    {
        _log = log;
        _enabled = options?.Value.TemporalHeuristicInvariantsEnabled ?? false;
    }

    /// <summary>
    /// Test constructor — invariant is always enabled. Used by existing
    /// SPEC + unit tests that exercise the invariant directly.
    /// </summary>
    public TemporalAnchorInvariant(ILogger<TemporalAnchorInvariant> log) : this(log, enabled: true) { }

    private TemporalAnchorInvariant(ILogger<TemporalAnchorInvariant> log, bool enabled)
    {
        _log = log;
        _enabled = enabled;
    }

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        // Step 2b flag-gating — no-op when disabled (default).
        if (!_enabled) return false;
        if (artifact.IntendedSink != CognitiveOutputSink.Dispatch)
            return false;

        return artifact.ProducerKind is
            CognitiveProducerKind.ConversationReply
         or CognitiveProducerKind.Outreach
         or CognitiveProducerKind.Voice;
    }

    public Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.Content))
            return Task.FromResult(InvariantResult.Pass());

        // Use artifact.GeneratedAt's DayOfWeek directly — it reflects the
        // OFFSET embedded in the value. Producers populate GeneratedAt with
        // an offset matching local time (DateTimeOffset.Now). Avoids
        // .ToLocalTime() which is TZ-machine-dependent.
        var localToday = artifact.GeneratedAt.DayOfWeek;
        var failure = CheckDayOfWeekClaims(artifact.Content, localToday);

        if (failure is not null)
        {
            _log.LogInformation(
                "TemporalAnchor: day-of-week claim mismatch — {Reason}", failure);
            return Task.FromResult(InvariantResult.Fail(failure));
        }

        return Task.FromResult(InvariantResult.Pass());
    }

    // ── Helpers (testable via InternalsVisibleTo) ────────────────────────

    private static readonly Dictionary<string, DayOfWeek> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "monday",    DayOfWeek.Monday    },
        { "tuesday",   DayOfWeek.Tuesday   },
        { "wednesday", DayOfWeek.Wednesday },
        { "thursday",  DayOfWeek.Thursday  },
        { "friday",    DayOfWeek.Friday    },
        { "saturday",  DayOfWeek.Saturday  },
        { "sunday",    DayOfWeek.Sunday    },
    };

    private const string DayAlt = @"(monday|tuesday|wednesday|thursday|friday|saturday|sunday)";

    // Pattern 1: "today is [day]" — direct present-tense claim. The
    // strongest signal — no ambiguity on what's being asserted.
    private static readonly Regex TodayIsRegex = new(
        @"\btoday\s+is\s+(?:a\s+)?" + DayAlt + @"\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pattern 2: "happy [day]" — greeting form. Common in casual register.
    private static readonly Regex HappyDayRegex = new(
        @"\bhappy\s+" + DayAlt + @"\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pattern 3: "this [day]" followed by present-tense / today-reference
    // markers (is | feels | seems | looks | already | might | one is).
    // The May 2 cluster's *"Well this one feels like it might be true
    // already"* doesn't directly mention a day — caught by sub-claim 1.
    // This pattern catches the explicit form *"this Sunday is..."*.
    private static readonly Regex ThisDayRegex = new(
        @"\bthis\s+" + DayAlt + @"\s+(?:is|feels|seems|looks|already|might|one\s+is)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Pure helper: given the artifact content and the actual current
    /// day-of-week (local), return null if no day-of-week claim is
    /// definitively wrong, or a human-readable failure reason if so.
    /// </summary>
    internal static string? CheckDayOfWeekClaims(string content, DayOfWeek today)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var todayMatch = TodayIsRegex.Match(content);
        if (todayMatch.Success)
        {
            var asserted = DayNames[todayMatch.Groups[1].Value];
            if (asserted != today)
                return $"asserted \"today is {asserted}\" but actual day-of-week is {today}";
        }

        var happyMatch = HappyDayRegex.Match(content);
        if (happyMatch.Success)
        {
            var asserted = DayNames[happyMatch.Groups[1].Value];
            if (asserted != today)
                return $"\"happy {asserted}\" greeting but actual day-of-week is {today}";
        }

        var thisDayMatch = ThisDayRegex.Match(content);
        if (thisDayMatch.Success)
        {
            var asserted = DayNames[thisDayMatch.Groups[1].Value];
            if (asserted != today)
                return $"\"this {asserted}\" used in present-tense form but actual day-of-week is {today}";
        }

        return null;
    }
}
