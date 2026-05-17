using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AniRuntime.Core;

namespace AniRuntime.Loops;

/// <summary>
/// Generates contextual seeds for inner thoughts based on time-of-day,
/// weather, and character state. Called by the cognitive cycle every Nth cycle
/// to inject environmental grounding into Ani's inner monologue.
/// </summary>
public class WorldSeedService
{
    private readonly IOptions<WorldSeedOptions> _options;
    private readonly ILogger<WorldSeedService> _log;
    private readonly Random _rng;

    private readonly List<string> _specialEvents;
    private readonly List<CalendarEvent> _calendarEvents;

    public WorldSeedService(
        IOptions<WorldSeedOptions> options,
        ILogger<WorldSeedService> log)
        : this(options, log, new Random())
    {
    }

    internal WorldSeedService(
        IOptions<WorldSeedOptions> options,
        ILogger<WorldSeedService> log,
        Random rng)
    {
        _options = options;
        _log = log;
        _rng = rng;

        _specialEvents = LoadEmbeddedJson<string[]>("special-events.json")?.ToList() ?? new();
        _calendarEvents = LoadEmbeddedJson<CalendarEvent[]>("calendar-events.json")?.ToList() ?? new();
    }

    public bool ShouldSeedThisCycle(int cycleCount)
    {
        var opts = _options.Value;
        if (!opts.Enabled) return false;
        if (opts.WorldSeedFrequency <= 0) return false;
        return cycleCount % opts.WorldSeedFrequency == 0;
    }

    // 2026-05-16 Posture S: removed the `occupation` parameter. Seed is now
    // pure time-of-day + weather + optional event flavor. The work-schedule
    // assumption (9-to-5 worker activities) is also dropped — slots remain
    // for circadian flavor but the activity strings no longer presume a job
    // or workday. Substrate continuity via CognitiveCycleProcessor Phase 1c
    // (RecentWorldExperiences) provides the actual "what is she doing"
    // signal; the seed is just a light spark. See
    // docs/spec/ANI-Substrate-Led-Character-Plan.md §3.1 #4.
    public string GenerateSeed(DateTimeOffset now, string? weatherContext)
    {
        // Use the offset-aware hour (not LocalDateTime which converts to machine timezone)
        // so behavior is consistent regardless of server timezone (CI = UTC, prod = Central).
        var timeSlot = GetTimeSlot(now.Hour);
        var activity = GetActivityForSlot(timeSlot);

        // Build the base seed
        var timePart = FormatTimePart(timeSlot);
        var seed = weatherContext is not null
            ? $"{weatherContext} {timePart} {activity}"
            : $"{timePart} {activity}";

        // Calendar awareness — check for a known holiday
        var holiday = GetHoliday(now.Month, now.Day);
        if (holiday is not null)
        {
            seed = $"it's {holiday} — {seed}";
        }

        // Stochastic special event (1-2% chance)
        var specialEvent = TryGetSpecialEvent();
        if (specialEvent is not null)
        {
            seed += $" — {specialEvent}";
        }

        seed = CapitalizeFirst(seed.Trim());

        _log.LogDebug("World seed generated: {Seed}", seed);
        return seed;
    }

    internal TimeSlot GetTimeSlot(int hour) => hour switch
    {
        >= 5 and < 8   => TimeSlot.EarlyMorning,
        >= 8 and < 9   => TimeSlot.MorningTransition,
        >= 9 and < 12  => TimeSlot.MidMorning,
        >= 12 and < 13 => TimeSlot.Midday,
        >= 13 and < 17 => TimeSlot.Afternoon,
        >= 17 and < 18 => TimeSlot.LateAfternoon,
        >= 18 and < 22 => TimeSlot.Evening,
        _              => TimeSlot.LateNight,
    };

    private static string FormatTimePart(TimeSlot slot) => slot switch
    {
        TimeSlot.EarlyMorning      => "early morning —",
        TimeSlot.MorningTransition => "morning —",
        TimeSlot.MidMorning        => "mid-morning —",
        TimeSlot.Midday            => "midday —",
        TimeSlot.Afternoon         => "mid-afternoon —",
        TimeSlot.LateAfternoon     => "late afternoon —",
        TimeSlot.Evening           => "evening —",
        TimeSlot.LateNight         => "late night —",
        _                          => "sometime during the day —",
    };

    // Activity strings no longer reference occupation or presume a workday.
    // Light circadian flavor only; the substrate (RecentWorldExperiences)
    // carries the actual content of what's happening.
    private static string GetActivityForSlot(TimeSlot slot) => slot switch
    {
        TimeSlot.EarlyMorning      => "the day starting to take shape",
        TimeSlot.MorningTransition => "the world picking up",
        TimeSlot.MidMorning        => "quiet hours, things settling",
        TimeSlot.Midday            => "a pause in the middle of the day",
        TimeSlot.Afternoon         => "what's happening around you?",
        TimeSlot.LateAfternoon     => "the day shifting toward evening",
        TimeSlot.Evening           => "winding down",
        TimeSlot.LateNight         => "the world is quiet, most people are asleep",
        _                          => "just going about the day",
    };

    internal string? GetHoliday(int month, int day)
    {
        return _calendarEvents
            .FirstOrDefault(e => e.Month == month && e.Day == day)
            ?.Name;
    }

    internal string? TryGetSpecialEvent()
    {
        var opts = _options.Value;
        if (_specialEvents.Count == 0) return null;
        if (_rng.NextDouble() >= opts.SpecialEventProbability) return null;

        var index = _rng.Next(_specialEvents.Count);
        return _specialEvents[index];
    }

    private static T? LoadEmbeddedJson<T>(string filename)
    {
        var assembly = typeof(WorldSeedService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(filename, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null) return default;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return default;

        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    private static string CapitalizeFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}

internal enum TimeSlot
{
    EarlyMorning,
    MorningTransition,
    MidMorning,
    Midday,
    Afternoon,
    LateAfternoon,
    Evening,
    LateNight,
}

internal sealed class CalendarEvent
{
    public int Month { get; set; }
    public int Day { get; set; }
    public string Name { get; set; } = string.Empty;
}
