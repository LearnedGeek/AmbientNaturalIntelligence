using System.Text.RegularExpressions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Door B Truth-Verification, Sub-claim 3 (May 2, 2026) — state-now
/// claim verification. Catches outbound content asserting Mark is in a
/// state (work, post-evening, etc.) that doesn't align with the local
/// clock or the day-of-week.
///
/// **The canonical case:** May 2 11:58 outreach *"hope your evening went
/// smooth - how was work?"* said on Saturday at 11:58 AM CDT. Two
/// errors in one sentence — evening hasn't happened yet, and Saturday
/// isn't a workday. Existing claim verifier passed both because they're
/// shape-coherent.
///
/// **Patterns caught:**
/// 1. **Past-tense time-of-day claims about periods not yet occurred**
///    — "hope your evening went smooth" / "how was your night?" said
///    when the named period's end time is later than current local time.
/// 2. **Work-state claims on weekends or before workday-end** —
///    "how was work?" / "hope work went well" said on Saturday/Sunday,
///    OR on a weekday but before ~4 PM (work day not yet complete).
///
/// **Patterns NOT caught (deliberately):**
/// - Present-tense ambiguous: "how's your morning?" said at 10 AM is
///   legitimate; only past-tense forms get flagged.
/// - Forward-looking: "what's your evening look like?" — future-leaning,
///   not a past-state claim.
/// - Generic work references: "how's the work coming?" — could be
///   home project, not workday-specific.
/// - Work claims with overriding perception evidence: V1 of this
///   invariant doesn't read perceptions; if a future invariant version
///   surfaces *"Mark is at the office today"* as an artifact context
///   field, this invariant should consult it before failing.
///
/// **Type-conditional applicability:** Dispatch sinks for
/// ConversationReply / Outreach / Voice. Same set as Door C and
/// TemporalAnchor sub-claim 2.
///
/// **Heuristic-only V1**: this invariant doesn't query the substrate.
/// Day-of-week + time-of-day heuristics are enough for the May 2 class.
/// If false positives appear (e.g., Mark genuinely worked Saturday and
/// the perception substrate shows it), upgrade by adding a
/// <c>RecentPerceptions</c> context field to CognitiveArtifact and
/// consulting it before failing on weekends.
/// </summary>
public sealed class StateNowInvariant : ICognitiveOutputInvariant
{
    private readonly ILogger<StateNowInvariant> _log;

    public string Name => "state-now";

    /// <summary>
    /// Workday end hour (local time, 24h). Past this hour on a weekday,
    /// "how was work?" is a legitimate retrospective reference. Default
    /// 16 (4 PM) — covers most office schedules; tune via configuration
    /// if the heuristic over- or under-fires.
    /// </summary>
    public const int WorkdayEndHour = 16;

    /// <summary>
    /// Workday start hour. Before this, even a weekday "how was work?"
    /// is wrong — work hasn't started yet. Default 6 (6 AM).
    /// </summary>
    public const int WorkdayStartHour = 6;

    public StateNowInvariant(ILogger<StateNowInvariant> log)
    {
        _log = log;
    }

    public bool AppliesTo(CognitiveArtifact artifact)
    {
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

        // Use artifact.GeneratedAt directly — its .Hour and .DayOfWeek
        // reflect the OFFSET embedded in the value. Producers populate
        // GeneratedAt with an offset matching local time (DateTimeOffset.Now,
        // not UtcNow). Avoids .ToLocalTime() which is TZ-machine-dependent
        // and breaks cross-machine determinism.
        var localNow = artifact.GeneratedAt;

        var timeOfDayFail = CheckPastTenseTimeOfDay(artifact.Content, localNow);
        if (timeOfDayFail is not null)
        {
            _log.LogInformation(
                "StateNow: past-tense time-of-day claim about not-yet-occurred period — {Reason}",
                timeOfDayFail);
            return Task.FromResult(InvariantResult.Fail(timeOfDayFail));
        }

        var workFail = CheckWorkStateClaim(artifact.Content, localNow);
        if (workFail is not null)
        {
            _log.LogInformation(
                "StateNow: work-state claim out of plausible window — {Reason}",
                workFail);
            return Task.FromResult(InvariantResult.Fail(workFail));
        }

        return Task.FromResult(InvariantResult.Pass());
    }

    // ── Pattern A: past-tense time-of-day on not-yet-occurred period ─────

    /// <summary>
    /// Time period boundaries (local hours, 24h). Past-tense references
    /// (*"hope your evening went..."*) are valid only if the END hour of
    /// the named period has already passed in local time.
    /// </summary>
    internal static readonly Dictionary<string, (int Start, int End)> TimePeriods = new(StringComparer.OrdinalIgnoreCase)
    {
        { "morning",   (5,  12) },
        { "afternoon", (12, 17) },
        { "evening",   (17, 21) },
        { "night",     (21, 24) },  // "night" treated as 21:00–midnight; 0–5 is also "night" but we don't fail on those
    };

    // Catches "how was your morning?", "hope your evening went smooth",
    // "did your night go well?", "how did your afternoon go?". The leading
    // verb form (was/did/hope) is the past-tense / retrospection signal.
    private static readonly Regex PastTenseTimeOfDayRegex = new(
        @"\b(?:how\s+was|hope|how\s+did|did)\s+(?:your\s+)?(morning|afternoon|evening|night)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Alt form: "your evening went smooth", "morning went well" — explicit
    // past-tense statement without a leading question marker.
    private static readonly Regex YourPeriodWentRegex = new(
        @"\b(?:your\s+)?(morning|afternoon|evening|night)\s+went\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string? CheckPastTenseTimeOfDay(string content, DateTimeOffset localNow)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var match = PastTenseTimeOfDayRegex.Match(content);
        if (!match.Success) match = YourPeriodWentRegex.Match(content);
        if (!match.Success) return null;

        var period = match.Groups[1].Value.ToLowerInvariant();
        if (!TimePeriods.TryGetValue(period, out var bounds)) return null;

        // Past-tense reference is valid only if the period's END hour has passed.
        // For "night" (21–24), the END is midnight — past-tense after midnight is fine.
        // We keep this conservative: only fail if current hour is BEFORE the period's
        // start hour (clearly hasn't happened yet). Mid-period or post-period is
        // ambiguous-but-passable.
        var currentHour = localNow.Hour;
        if (currentHour < bounds.Start)
        {
            return $"asserted past-tense \"{period}\" but current local hour is {currentHour:D2}:00 " +
                   $"(period starts at {bounds.Start:D2}:00 — hasn't happened yet)";
        }

        return null;
    }

    // ── Pattern B: work-state claims ─────────────────────────────────────

    private static readonly Regex WorkStateClaimRegex = new(
        @"\b(?:how\s+was|how's|hope)\s+(?:your\s+)?work(?:\s+day)?\b|" +
        @"\byour\s+work\s+day\b|" +
        @"\byou're\s+at\s+(?:the\s+office|work)\b|" +
        @"\bhope\s+work\s+went\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string? CheckWorkStateClaim(string content, DateTimeOffset localNow)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        if (!WorkStateClaimRegex.IsMatch(content)) return null;

        var dayOfWeek = localNow.DayOfWeek;
        var hour = localNow.Hour;

        // Weekend — work-state claim fails (in absence of perception evidence to override).
        if (dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return $"work-state claim on a {dayOfWeek} (no perception evidence Mark is working today)";
        }

        // Weekday but before workday-end — claim is ambiguous if currently mid-workday,
        // but past-tense forms ("how was work") imply work is COMPLETE.
        if (hour < WorkdayEndHour)
        {
            return $"past-tense work-state claim on a {dayOfWeek} at local hour {hour:D2}:00 " +
                   $"(workday-end heuristic = {WorkdayEndHour:D2}:00 — work-day not yet complete)";
        }

        // Weekday after workday-end — legitimate retrospective reference.
        return null;
    }
}
