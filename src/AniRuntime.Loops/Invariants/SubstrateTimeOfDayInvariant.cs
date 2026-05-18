using System.Text.RegularExpressions;
using AniRuntime.Core.Utilities;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Door B Truth-Verification, Sub-claim 5 (May 3, 2026) — present-tense
/// time-of-day verification. Catches outbound content asserting a
/// time-of-day descriptor inconsistent with the local clock (e.g. "i
/// know it's late" said at 6:47 AM, "good morning" said at 11 PM).
///
/// **Distinct from sub-claim 3 (StateNowInvariant):** sub-claim 3
/// catches PAST-TENSE state queries about not-yet-occurred periods
/// ("hope your evening went smooth" at 11:58 AM). This sub-claim
/// catches PRESENT-TENSE assertions about the current time-of-day
/// ("good morning" / "it's late") that don't match the wall clock.
/// Both invariants apply to the same producer/sink set and are run
/// independently by the gate.
///
/// **The canonical case:** May 3 06:47 outreach *"hey perez🥰… i know
/// it's late but did you actually get that cold brew or were you just
/// testing me for my salted caramel bias?"* The system prompt at 06:47
/// correctly told the model "RIGHT NOW it is 6:47 AM," but the model
/// composed "i know it's late" by lifting time-of-day language from
/// substrate (a recent inner thought from 30 minutes earlier mentioned
/// "10:30" — which was 10:30 PM the night before, but became time-of-
/// day substrate priming for the 6:47 AM composition). Time-of-day
/// language in output is a typed claim with an absolute oracle (the
/// system clock); substrate priming should not override it.
///
/// **Patterns caught (case-insensitive, present-tense form):**
/// - <c>good morning</c> / <c>good afternoon</c> / <c>good evening</c> /
///   <c>good night</c> — fail when current local hour is outside the
///   period's window.
/// - <c>it's late</c> / <c>so late</c> / <c>this late</c> /
///   <c>i know it's late</c> / <c>kinda late</c> — fail when current
///   local hour is outside the late-night window (defined as 21:00 –
///   05:00, wrap-around).
/// - <c>early in the morning</c> / <c>so early</c> — fail when current
///   local hour is outside the early-morning window (04:00 – 09:00).
///
/// **Patterns NOT caught (deliberately):**
/// - Past-tense forms ("good morning was rough"): handled by sub-claim 3.
/// - Conditional / forward-looking ("when it gets late tonight"): not
///   asserting a present time-of-day.
/// - Generic temporal references ("i'm always tired in the evening"):
///   not a present-tense self-anchored time-of-day claim.
///
/// **Type-conditional applicability:** Dispatch sinks for
/// ConversationReply / Outreach / Voice. Same set as Door C and Door B
/// sub-claims 1/2/3.
///
/// **Local time source:** uses <c>artifact.GeneratedAt</c> directly
/// (DateTimeOffset with embedded offset) — same convention as
/// StateNowInvariant and TemporalAnchorInvariant. Producers populate
/// GeneratedAt with <c>DateTimeOffset.Now</c> to capture local time.
/// </summary>
public sealed class SubstrateTimeOfDayInvariant : ICognitiveOutputInvariant
{
    private readonly ILogger<SubstrateTimeOfDayInvariant> _log;
    private readonly bool _enabled;

    public string Name => "substrate-time-of-day";

    /// <summary>Production constructor — Step 2b flag-gated.</summary>
    public SubstrateTimeOfDayInvariant(ILogger<SubstrateTimeOfDayInvariant> log, IOptions<AniOptions> options)
    {
        _log = log;
        _enabled = options?.Value.TemporalHeuristicInvariantsEnabled ?? false;
    }

    /// <summary>Test constructor — always enabled.</summary>
    public SubstrateTimeOfDayInvariant(ILogger<SubstrateTimeOfDayInvariant> log) : this(log, enabled: true) { }

    private SubstrateTimeOfDayInvariant(ILogger<SubstrateTimeOfDayInvariant> log, bool enabled)
    {
        _log = log;
        _enabled = enabled;
    }

    public bool AppliesTo(CognitiveArtifact artifact)
    {
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

        var localNow = artifact.GeneratedAt;
        var hour = localNow.Hour;

        var greetingFail = CheckGreetingTimeOfDay(artifact.Content, hour);
        if (greetingFail is not null)
        {
            _log.LogInformation(
                "SubstrateTimeOfDay: greeting time-of-day mismatch — {Reason}",
                greetingFail);
            return Task.FromResult(InvariantResult.Fail(greetingFail));
        }

        var lateFail = CheckLateClaim(artifact.Content, hour);
        if (lateFail is not null)
        {
            _log.LogInformation(
                "SubstrateTimeOfDay: \"late\" claim mismatch — {Reason}", lateFail);
            return Task.FromResult(InvariantResult.Fail(lateFail));
        }

        var earlyFail = CheckEarlyClaim(artifact.Content, hour);
        if (earlyFail is not null)
        {
            _log.LogInformation(
                "SubstrateTimeOfDay: \"early\" claim mismatch — {Reason}", earlyFail);
            return Task.FromResult(InvariantResult.Fail(earlyFail));
        }

        return Task.FromResult(InvariantResult.Pass());
    }

    // ── Pattern A: greeting time-of-day ──────────────────────────────────

    /// <summary>
    /// Time period boundaries (local hours, 24h) for present-tense
    /// "good X" greetings. Generous windows to avoid false positives at
    /// edges; only flag CLEAR mismatches.
    /// </summary>
    internal static readonly Dictionary<string, (int StartInclusive, int EndExclusive)> GreetingWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        { "morning",   (4,  12) },  // 4 AM – noon
        { "afternoon", (12, 18) },  // noon – 6 PM
        { "evening",   (17, 22) },  // 5 PM – 10 PM (slight overlap with afternoon at edge)
        // "night" handled separately because it wraps around midnight
    };

    private static readonly Regex GoodGreetingRegex = new(
        @"\bgood\s+(morning|afternoon|evening|night)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Hour windows for "good night" — 20:00–05:00 wrap-around. Generous
    /// because "good night" said at 7:30 PM as a sign-off is normal.
    /// </summary>
    internal static (int From, int To) GoodNightWindow = (20, 5);

    internal static string? CheckGreetingTimeOfDay(string content, int currentHour)
    {
        var match = GoodGreetingRegex.Match(content);
        if (!match.Success) return null;

        var period = match.Groups[1].Value.ToLowerInvariant();

        if (string.Equals(period, "night", StringComparison.OrdinalIgnoreCase))
        {
            // Wrap-around: 20–24 OR 0–5 is OK; 5–20 is mismatch.
            if (currentHour >= GoodNightWindow.From || currentHour < GoodNightWindow.To)
                return null;
            return $"asserted \"good night\" but current local hour is {currentHour:D2}:00 " +
                   $"(window: {GoodNightWindow.From:D2}:00–24:00 or 00:00–{GoodNightWindow.To:D2}:00)";
        }

        if (!GreetingWindows.TryGetValue(period, out var bounds)) return null;
        if (currentHour >= bounds.StartInclusive && currentHour < bounds.EndExclusive)
            return null;

        return $"asserted \"good {period}\" but current local hour is {currentHour:D2}:00 " +
               $"(window: {bounds.StartInclusive:D2}:00–{bounds.EndExclusive:D2}:00)";
    }

    // ── Pattern B: "late" claim ──────────────────────────────────────────

    /// <summary>
    /// Late-night window (local hours, 24h). "It's late" is plausible
    /// from 21:00 to 03:00 (wrap-around).
    /// </summary>
    internal static (int From, int To) LateWindow = (21, 3);

    private static readonly Regex LateClaimRegex = new(
        @"\b(?:it'?s|its|so|this|kinda|i\s+know\s+it'?s|i\s+know\s+its)\s+late\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string? CheckLateClaim(string content, int currentHour)
    {
        if (!LateClaimRegex.IsMatch(content)) return null;

        // Wrap-around: 21–24 OR 0–3 is "late"; 3–21 is not.
        if (currentHour >= LateWindow.From || currentHour < LateWindow.To)
            return null;

        return $"asserted \"it's late\" but current local hour is {currentHour:D2}:00 " +
               $"(late window: {LateWindow.From:D2}:00–24:00 or 00:00–{LateWindow.To:D2}:00). " +
               $"This is the May 3 06:47 \"i know it's late\" failure shape — substrate-time-of-day priming overrode the system clock.";
    }

    // ── Pattern C: "early" claim ─────────────────────────────────────────

    /// <summary>
    /// Early-morning window (local hours, 24h). "It's early" / "so early"
    /// makes sense from 04:00 to 09:00.
    /// </summary>
    internal static (int From, int To) EarlyWindow = (4, 9);

    private static readonly Regex EarlyClaimRegex = new(
        @"\b(?:so\s+early|early\s+(?:in\s+the\s+)?morning|it'?s\s+early|its\s+early)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string? CheckEarlyClaim(string content, int currentHour)
    {
        if (!EarlyClaimRegex.IsMatch(content)) return null;

        if (currentHour >= EarlyWindow.From && currentHour < EarlyWindow.To)
            return null;

        return $"asserted \"early\" but current local hour is {currentHour:D2}:00 " +
               $"(early window: {EarlyWindow.From:D2}:00–{EarlyWindow.To:D2}:00)";
    }
}
