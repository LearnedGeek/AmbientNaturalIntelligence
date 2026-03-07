using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Perception;

public sealed class TimePerceptionSource : IPerceptionSource
{
    private readonly TimeProvider _time;

    public string             SourceName => "time";
    public PerceptionCategory Category   => PerceptionCategory.Environment;
    public bool               IsEnabled  => true;

    public TimePerceptionSource(TimeProvider time) => _time = time;

    public Task<IEnumerable<PerceptionEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var now    = _time.GetLocalNow();
        var events = new List<PerceptionEvent>();

        // Base context — always emitted
        events.Add(Evt($"It is {DescribeTimeOfDay(now)} on {now.DayOfWeek} in {DescribeMonthPosition(now)}."));

        // Day-of-week conditions
        if (now.DayOfWeek == DayOfWeek.Monday && now.Hour < 12)
            events.Add(Evt("A new week is beginning."));
        else if (now.DayOfWeek == DayOfWeek.Friday && now.Hour >= 14)
            events.Add(Evt("The week is almost over — a natural time to reflect."));
        else if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            events.Add(Evt("It is the weekend — a slower pace than usual."));

        // Month boundary
        if (now.Day == 1)
            events.Add(Evt($"A new month is beginning — {now:MMMM}."));

        // Season transition (first 3 days only — otherwise noise)
        var season = DescribeSeasonTransition(now);
        if (season is not null)
            events.Add(Evt(season));

        // Nearby holidays
        var holiday = NearestHoliday(now);
        if (holiday is not null)
            events.Add(Evt(holiday));

        // Elapsed since last cycle
        var elapsed = now - since;
        if (elapsed.TotalHours >= 2.0)
        {
            var h = (int)Math.Round(elapsed.TotalHours);
            events.Add(Evt($"It has been about {h} hour{(h == 1 ? "" : "s")} since the last thought."));
        }

        return Task.FromResult<IEnumerable<PerceptionEvent>>(events);
    }

    private PerceptionEvent Evt(string summary) => new()
    {
        SourceName    = SourceName,
        Category      = Category,
        Summary       = summary,
        MarkRelevance = 0.1f,
        OccurredAt    = _time.GetLocalNow(),
    };

    private static string DescribeTimeOfDay(DateTimeOffset t) => t.Hour switch
    {
        < 6  => "late night",
        < 9  => "early morning",
        < 12 => "morning",
        < 14 => "early afternoon",
        < 17 => "afternoon",
        < 20 => "evening",
        _    => "late evening",
    };

    private static string DescribeMonthPosition(DateTimeOffset t)
    {
        var month = t.ToString("MMMM");
        return t.Day switch
        {
            <= 10 => $"early {month}",
            <= 20 => $"mid-{month}",
            _     => $"late {month}",
        };
    }

    private static string? DescribeSeasonTransition(DateTimeOffset t)
    {
        if (t.Day > 3) return null;
        return (t.Month, t.Day) switch
        {
            (12, _) => "Winter is just beginning.",
            (3,  _) => "Spring is just beginning.",
            (6,  _) => "Summer is just beginning.",
            (9,  _) => "Fall is just beginning.",
            _        => null,
        };
    }

    private static string? NearestHoliday(DateTimeOffset t)
    {
        var today = DateOnly.FromDateTime(t.DateTime);
        var year  = today.Year;

        var holidays = new[]
        {
            (new DateOnly(year,  1,  1), "New Year's Day"),
            (new DateOnly(year,  2, 14), "Valentine's Day"),
            (new DateOnly(year,  7,  4), "Independence Day"),
            (new DateOnly(year, 10, 31), "Halloween"),
            (new DateOnly(year, 11, 28), "Thanksgiving"),   // approximate — 4th Thursday
            (new DateOnly(year, 12, 24), "Christmas Eve"),
            (new DateOnly(year, 12, 25), "Christmas Day"),
            (new DateOnly(year, 12, 31), "New Year's Eve"),
        };

        foreach (var (date, name) in holidays)
        {
            var diff = (date.ToDateTime(TimeOnly.MinValue) - today.ToDateTime(TimeOnly.MinValue)).Days;
            if (diff == 0)            return $"Today is {name}.";
            if (diff is >= 1 and <= 7) return $"{name} is {diff} day{(diff == 1 ? "" : "s")} away.";
        }

        return null;
    }
}
