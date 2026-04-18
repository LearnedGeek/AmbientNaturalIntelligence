using System.Net.Http.Json;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Perception;

public sealed class WeatherPerceptionSource : IPerceptionSource
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly WeatherOptions _options;
    private readonly ILogger<WeatherPerceptionSource> _log;

    private string? _lastCondition;
    private float? _lastTemp;
    private DateTimeOffset _lastEmitted = DateTimeOffset.MinValue;

    public string             SourceName => "weather";
    public PerceptionCategory Category   => PerceptionCategory.Environment;
    public bool               IsEnabled  => _options.Enabled;

    public WeatherPerceptionSource(
        IHttpClientFactory httpFactory,
        IOptions<WeatherOptions> options,
        ILogger<WeatherPerceptionSource> log)
    {
        _httpFactory = httpFactory;
        _options     = options.Value;
        _log         = log;
    }

    public async Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        // Only poll every 30 minutes — weather doesn't change that fast
        if (DateTimeOffset.UtcNow - _lastEmitted < TimeSpan.FromMinutes(_options.PollIntervalMinutes))
            return [];

        try
        {
            var client = _httpFactory.CreateClient("weather");
            var url = $"https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={_options.Latitude}" +
                      $"&longitude={_options.Longitude}" +
                      $"&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m" +
                      $"&temperature_unit=fahrenheit" +
                      $"&wind_speed_unit=mph" +
                      $"&timezone=America%2FChicago";

            var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
            var current = json.GetProperty("current");

            var temp      = current.GetProperty("temperature_2m").GetSingle();
            var humidity  = current.GetProperty("relative_humidity_2m").GetInt32();
            var windSpeed = current.GetProperty("wind_speed_10m").GetSingle();
            var code      = current.GetProperty("weather_code").GetInt32();

            var condition = DescribeWeatherCode(code);
            var events    = new List<PerceptionEvent>();

            // Only emit current conditions if this is the first poll or something changed.
            // Repeating "45°F and partly cloudy" every 30 min anchors the inner thought
            // model on weather even when nothing interesting is happening.
            var tempChanged = _lastTemp.HasValue && Math.Abs(temp - _lastTemp.Value) >= 3;
            var condChanged = _lastCondition is not null && _lastCondition != condition;
            var isFirstPoll = !_lastTemp.HasValue;

            if (isFirstPoll || tempChanged || condChanged)
            {
                var summary = $"The weather outside is {temp:F0}°F and {condition.ToLower()}. " +
                              $"Wind {windSpeed:F0} mph, humidity {humidity}%.";
                events.Add(Evt(summary, 0.15f));
            }

            // Notable conditions get higher relevance
            if (temp <= 10)
                events.Add(Evt($"It is bitterly cold outside — {temp:F0}°F.", 0.3f));
            else if (temp <= 25)
                events.Add(Evt($"It is very cold outside — {temp:F0}°F.", 0.25f));
            else if (temp >= 95)
                events.Add(Evt($"It is dangerously hot outside — {temp:F0}°F.", 0.3f));
            else if (temp >= 85)
                events.Add(Evt($"It is quite hot outside — {temp:F0}°F.", 0.25f));

            if (windSpeed >= 30)
                events.Add(Evt($"It is very windy — {windSpeed:F0} mph gusts.", 0.25f));

            if (code is >= 95 and <= 99)
                events.Add(Evt("There is a thunderstorm happening.", 0.35f));
            else if (code is >= 71 and <= 77)
                events.Add(Evt("It is snowing outside.", 0.3f));

            // Detect significant change from last poll
            if (_lastTemp.HasValue && Math.Abs(temp - _lastTemp.Value) >= 10)
            {
                var direction = temp > _lastTemp.Value ? "warmed up" : "dropped";
                events.Add(Evt($"The temperature has {direction} significantly — was {_lastTemp.Value:F0}°F, now {temp:F0}°F.", 0.3f));
            }

            if (_lastCondition is not null && _lastCondition != condition)
                events.Add(Evt($"The weather has changed — was {_lastCondition.ToLower()}, now {condition.ToLower()}.", 0.2f));

            _lastTemp      = temp;
            _lastCondition = condition;
            _lastEmitted   = DateTimeOffset.UtcNow;

            _log.LogDebug("Weather: {Temp}°F, {Condition}, wind {Wind} mph", temp, condition, windSpeed);

            return events;
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("Weather API unreachable: {Message}", ex.Message);
            return [];
        }
        catch (TaskCanceledException)
        {
            _log.LogWarning("Weather API timeout");
            return [];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Weather perception failed — skipping");
            return [];
        }
    }

    private PerceptionEvent Evt(string summary, float relevance) => new()
    {
        SourceName       = SourceName,
        Category         = Category,
        Summary          = summary,
        ContactRelevance = relevance,
        OccurredAt       = DateTimeOffset.UtcNow,
    };

    /// <summary>WMO weather code → human-readable description.</summary>
    private static string DescribeWeatherCode(int code) => code switch
    {
        0           => "Clear sky",
        1           => "Mainly clear",
        2           => "Partly cloudy",
        3           => "Overcast",
        45 or 48    => "Foggy",
        51          => "Light drizzle",
        53          => "Moderate drizzle",
        55          => "Dense drizzle",
        56 or 57    => "Freezing drizzle",
        61          => "Light rain",
        63          => "Moderate rain",
        65          => "Heavy rain",
        66 or 67    => "Freezing rain",
        71          => "Light snow",
        73          => "Moderate snow",
        75          => "Heavy snow",
        77          => "Snow grains",
        80          => "Light rain showers",
        81          => "Moderate rain showers",
        82          => "Violent rain showers",
        85          => "Light snow showers",
        86          => "Heavy snow showers",
        95          => "Thunderstorm",
        96 or 99    => "Thunderstorm with hail",
        _           => "Unknown conditions",
    };
}
