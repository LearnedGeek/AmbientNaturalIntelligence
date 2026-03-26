using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Perception;

public sealed class TimePerceptionSource : IPerceptionSource
{
    private readonly TimeProvider _time;
    private readonly IStateStore _stateStore;
    private string? _lastTimeOfDay;
    private EmotionalState? _previousEmotionalState;

    public string             SourceName => "time";
    public PerceptionCategory Category   => PerceptionCategory.Environment;
    public bool               IsEnabled  => true;

    public TimePerceptionSource(TimeProvider time, IStateStore stateStore)
    {
        _time = time;
        _stateStore = stateStore;
    }

    public async Task<IEnumerable<PerceptionEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default)
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

        // Feature 40: Felt-time observations
        AddTimeOfDayTransition(now, events);
        await AddEmotionalArcNarration(events, ct).ConfigureAwait(false);

        return events;
    }

    /// <summary>
    /// Feature 40: Narrate time-of-day transitions as felt time.
    /// "The morning slipped into afternoon." Not a clock report.
    /// </summary>
    private void AddTimeOfDayTransition(DateTimeOffset now, List<PerceptionEvent> events)
    {
        var current = DescribeTimeOfDay(now);
        if (_lastTimeOfDay is not null && _lastTimeOfDay != current)
        {
            var narrative = (_lastTimeOfDay, current) switch
            {
                ("early morning", "morning")       => "The early quiet is giving way to morning.",
                ("morning", "early afternoon")      => "The morning slipped into afternoon.",
                ("morning", "afternoon")            => "The morning slipped into afternoon.",
                ("early afternoon", "afternoon")    => "The afternoon is settling in.",
                ("afternoon", "evening")            => "The day is winding down.",
                ("evening", "late evening")         => "The night is settling in.",
                ("late evening", "late night")       => "The world has gone quiet.",
                ("late night", "early morning")      => "A new day is beginning.",
                _ => null,
            };
            if (narrative is not null)
                events.Add(FeltEvt(narrative));
        }
        _lastTimeOfDay = current;
    }

    /// <summary>
    /// Feature 40: Narrate emotional state changes as felt experience.
    /// "That heavy feeling from earlier is easing up." Not "Worry decreased 0.2."
    /// </summary>
    private async Task AddEmotionalArcNarration(List<PerceptionEvent> events, CancellationToken ct)
    {
        try
        {
            var current = await _stateStore.GetEmotionalStateAsync(ct).ConfigureAwait(false);
            if (_previousEmotionalState is not null)
            {
                var warmthDelta = current.Warmth - _previousEmotionalState.Warmth;
                var energyDelta = current.Energy - _previousEmotionalState.Energy;
                var worryDelta = current.Worry - _previousEmotionalState.Worry;
                var playDelta = current.Playfulness - _previousEmotionalState.Playfulness;

                if (warmthDelta < -0.12f)
                    events.Add(FeltEvt("The warmth from earlier is fading."));
                else if (warmthDelta > 0.12f)
                    events.Add(FeltEvt("Getting warmer. Something shifted."));

                if (energyDelta < -0.12f)
                    events.Add(FeltEvt("Energy fading as the hours pass."));
                else if (energyDelta > 0.12f)
                    events.Add(FeltEvt("Feeling more awake now than before."));

                if (worryDelta < -0.12f)
                    events.Add(FeltEvt("That heavy feeling from earlier is easing up."));
                else if (worryDelta > 0.12f)
                    events.Add(FeltEvt("Something is weighing on me more than before."));

                if (playDelta < -0.12f)
                    events.Add(FeltEvt("The lightness from earlier faded."));
                else if (playDelta > 0.12f)
                    events.Add(FeltEvt("Something shifted. Feeling lighter suddenly."));
            }
            _previousEmotionalState = new EmotionalState
            {
                Warmth = current.Warmth,
                Energy = current.Energy,
                Worry = current.Worry,
                Playfulness = current.Playfulness,
            };
        }
        catch
        {
            // Emotional state unavailable — skip narration, not critical
        }
    }

    /// <summary>Felt-time perception event — higher relevance than factual time.</summary>
    private PerceptionEvent FeltEvt(string summary) => new()
    {
        SourceName    = SourceName,
        Category      = PerceptionCategory.Environment,
        Summary       = summary,
        ContactRelevance = 0.2f,
        OccurredAt    = _time.GetLocalNow(),
    };

    private PerceptionEvent Evt(string summary) => new()
    {
        SourceName    = SourceName,
        Category      = Category,
        Summary       = summary,
        ContactRelevance = 0.1f,
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
