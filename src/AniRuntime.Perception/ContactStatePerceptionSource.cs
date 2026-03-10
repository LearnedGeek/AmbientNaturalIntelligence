using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Perception;

/// <summary>
/// Infers the primary contact's likely current state from their known routine,
/// the time of day, and interaction history. No external APIs — just a mental model
/// of their day.
///
/// This is the same thing any close friend does: you don't need a GPS ping to know
/// your partner is at work at 2 PM on a Tuesday. You just know because you know their life.
/// </summary>
public sealed class ContactStatePerceptionSource : IPerceptionSource
{
    private readonly IMemoryService _memory;
    private readonly TimeProvider   _time;

    public string             SourceName => "contact-state";
    public PerceptionCategory Category   => PerceptionCategory.Social;
    public bool               IsEnabled  => true;

    public ContactStatePerceptionSource(IMemoryService memory, TimeProvider time)
    {
        _memory = memory;
        _time   = time;
    }

    public async Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        var character   = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);
        var contactName = character.PrimaryContactName;
        var routine     = character.ContactRoutine;
        var now         = _time.GetLocalNow();
        var events      = new List<PerceptionEvent>();

        // What is the contact probably doing right now?
        var activity = InferCurrentActivity(routine, now);
        if (activity is not null)
            events.Add(Evt($"{contactName} is probably {activity}", 0.3f, now));

        // How long since last interaction?
        var lastInteraction = await GetLastInteractionAsync(ct).ConfigureAwait(false);
        if (lastInteraction is not null)
        {
            var gap = now - lastInteraction.Value;
            var gapEvent = DescribeInteractionGap(gap, now, character.PrimaryContactName);
            if (gapEvent is not null)
                events.Add(Evt(gapEvent, gap.TotalHours > 24 ? 0.5f : 0.2f, now));
        }

        // Is there something specific coming up today?
        var upcoming = InferUpcomingActivity(routine, now, contactName);
        if (upcoming is not null)
            events.Add(Evt(upcoming, 0.2f, now));

        return events;
    }

    private static string? InferCurrentActivity(ContactRoutine? routine, DateTimeOffset now)
    {
        if (routine is null) return null;

        var dayName = now.DayOfWeek.ToString();
        var timeNow = TimeOnly.FromDateTime(now.DateTime);

        // Check day-specific overrides first
        if (routine.DayOverrides.TryGetValue(dayName, out var overrides))
        {
            var match = FindClosestActivity(overrides, timeNow);
            if (match is not null) return match;
        }

        // Weekend has no default weekday schedule
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            // Only use dayOverrides for weekends — no weekday fallback
            return null;
        }

        return FindClosestActivity(routine.Weekday, timeNow);
    }

    private static string? FindClosestActivity(Dictionary<string, string> schedule, TimeOnly now)
    {
        string? bestActivity = null;
        TimeOnly bestTime = default;

        foreach (var (timeStr, activity) in schedule)
        {
            if (!TimeOnly.TryParse(timeStr, out var actTime)) continue;

            // Activity applies from its start time until the next one
            // So find the latest activity that's before or at the current time
            if (actTime <= now && (bestActivity is null || actTime > bestTime))
            {
                bestActivity = activity;
                bestTime = actTime;
            }
        }

        return bestActivity;
    }

    private static string? InferUpcomingActivity(ContactRoutine? routine, DateTimeOffset now, string contactName)
    {
        if (routine is null) return null;

        var dayName = now.DayOfWeek.ToString();
        var timeNow = TimeOnly.FromDateTime(now.DateTime);

        // Check for something coming up in the next 1-2 hours
        var schedule = new Dictionary<string, string>(routine.Weekday);

        // Merge day overrides
        if (routine.DayOverrides.TryGetValue(dayName, out var overrides))
        {
            foreach (var (k, v) in overrides)
                schedule[k] = v;
        }

        foreach (var (timeStr, activity) in schedule)
        {
            if (!TimeOnly.TryParse(timeStr, out var actTime)) continue;

            var minutesUntil = (actTime.ToTimeSpan() - timeNow.ToTimeSpan()).TotalMinutes;
            if (minutesUntil is > 30 and <= 120)
            {
                var hours = (int)(minutesUntil / 60);
                var mins  = (int)(minutesUntil % 60);
                var timeDesc = hours > 0
                    ? $"about {hours} hour{(hours == 1 ? "" : "s")} from now"
                    : $"about {mins} minutes from now";
                return $"{contactName} has something coming up {timeDesc}: {activity}";
            }
        }

        return null;
    }

    private async Task<DateTimeOffset?> GetLastInteractionAsync(CancellationToken ct)
    {
        // Look for the most recent episodic memory that involves outreach or communication
        var recent = await _memory.GetByTypeAsync(MemoryType.Episodic, 5, ct).ConfigureAwait(false);
        var lastOutreach = recent.FirstOrDefault(r =>
            r.Content.Contains("reached out", StringComparison.OrdinalIgnoreCase) ||
            r.Content.Contains("texted", StringComparison.OrdinalIgnoreCase) ||
            r.Content.Contains("replied", StringComparison.OrdinalIgnoreCase) ||
            r.Content.Contains("said:", StringComparison.OrdinalIgnoreCase));

        return lastOutreach?.OccurredAt;
    }

    private static string? DescribeInteractionGap(TimeSpan gap, DateTimeOffset now, string contactName)
    {
        var name = string.IsNullOrWhiteSpace(contactName) ? "them" : contactName;

        // Don't mention gaps during sleeping hours (11 PM - 7 AM)
        if (now.Hour is >= 23 or < 7 && gap.TotalHours < 10)
            return null;

        return gap.TotalHours switch
        {
            < 2   => null,  // Too recent to be notable
            < 6   => $"It's been a few hours since {name} and I last connected.",
            < 12  => $"{name} has been quiet today — probably just busy.",
            < 24  => $"Haven't heard from {name} since yesterday. Hope he's doing okay.",
            < 48  => $"It's been over a day since {name} and I talked. That's a little unusual.",
            _     => $"It's been {(int)gap.TotalDays} days since {name} and I connected. I miss him.",
        };
    }

    private PerceptionEvent Evt(string summary, float relevance, DateTimeOffset now) => new()
    {
        SourceName    = SourceName,
        Category      = Category,
        Summary       = summary,
        ContactRelevance = relevance,
        OccurredAt    = now,
    };
}
