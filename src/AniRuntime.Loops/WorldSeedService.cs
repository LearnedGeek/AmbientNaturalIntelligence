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

    public string GenerateSeed(DateTimeOffset now, string? weatherContext, string occupation)
    {
        // Use the offset-aware hour (not LocalDateTime which converts to machine timezone)
        // so behavior is consistent regardless of server timezone (CI = UTC, prod = Central).
        var timeSlot = GetTimeSlot(now.Hour);
        var activity = GetActivityForSlot(timeSlot, occupation);

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
        >= 5 and < 8   => TimeSlot.MorningRoutine,
        >= 8 and < 9   => TimeSlot.Commute,
        >= 9 and < 12  => TimeSlot.WorkEarly,
        >= 12 and < 13 => TimeSlot.Lunch,
        >= 13 and < 17 => TimeSlot.WorkAfternoon,
        >= 17 and < 18 => TimeSlot.CommuteHome,
        >= 18 and < 22 => TimeSlot.Evening,
        _              => TimeSlot.LateNight,
    };

    private static string FormatTimePart(TimeSlot slot) => slot switch
    {
        TimeSlot.MorningRoutine => "early morning —",
        TimeSlot.Commute        => "morning commute —",
        TimeSlot.WorkEarly      => "mid-morning at",
        TimeSlot.Lunch          => "lunchtime —",
        TimeSlot.WorkAfternoon  => "mid-afternoon at",
        TimeSlot.CommuteHome    => "heading home —",
        TimeSlot.Evening        => "evening at home —",
        TimeSlot.LateNight      => "late night —",
        _                       => "sometime during the day —",
    };

    private static string GetActivityForSlot(TimeSlot slot, string occupation) => slot switch
    {
        TimeSlot.MorningRoutine => "getting ready for the day",
        TimeSlot.Commute        => "on the way to work",
        TimeSlot.WorkEarly      => $"the {occupation} — settling into the day",
        TimeSlot.Lunch          => "taking a break, maybe grabbing something to eat",
        TimeSlot.WorkAfternoon  => $"the {occupation} — what's happening around you?",
        TimeSlot.CommuteHome    => "wrapping up the workday",
        TimeSlot.Evening        => "winding down",
        TimeSlot.LateNight      => "the world is quiet, most people are asleep",
        _                       => "just going about the day",
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
    MorningRoutine,
    Commute,
    WorkEarly,
    Lunch,
    WorkAfternoon,
    CommuteHome,
    Evening,
    LateNight,
}

internal sealed class CalendarEvent
{
    public int Month { get; set; }
    public int Day { get; set; }
    public string Name { get; set; } = string.Empty;
}
