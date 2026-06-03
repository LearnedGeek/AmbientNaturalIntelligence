using Microsoft.Extensions.Logging;

namespace AniRuntime.Eval;

/// <summary>
/// Custom <see cref="ILoggerProvider"/> that captures structured Theme O.2
/// pipeline telemetry events (<c>O_HANDLER_START</c>, <c>O_HANDLER_END</c>,
/// <c>O_PIPELINE_START</c>, <c>O_PIPELINE_END</c>) into an in-memory list
/// without affecting normal log output. The Eval driver inspects the
/// captured list after the cycle completes to produce structured per-stage
/// gate verdicts in the JSON result.
///
/// <para>
/// Runtime is not modified — this provider sits alongside the console
/// logger via the standard <see cref="ILoggerFactory"/> composition.
/// </para>
/// </summary>
internal sealed class GateTelemetryCapture : ILoggerProvider
{
    public List<GateEvent> Events { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(GateTelemetryCapture parent) : ILogger
    {
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> structured) return;

            string? template = null;
            foreach (var kvp in structured)
            {
                if (kvp.Key == "{OriginalFormat}")
                {
                    template = kvp.Value as string;
                    break;
                }
            }

            if (template is null) return;

            string? eventName = null;
            if (template.StartsWith("O_HANDLER_END ", StringComparison.Ordinal)) eventName = "O_HANDLER_END";
            else if (template.StartsWith("O_HANDLER_START ", StringComparison.Ordinal)) eventName = "O_HANDLER_START";
            else if (template.StartsWith("O_PIPELINE_START ", StringComparison.Ordinal)) eventName = "O_PIPELINE_START";
            else if (template.StartsWith("O_PIPELINE_END ", StringComparison.Ordinal)) eventName = "O_PIPELINE_END";

            if (eventName is null) return;

            var props = new Dictionary<string, string>(structured.Count);
            foreach (var kvp in structured)
            {
                if (kvp.Key == "{OriginalFormat}") continue;
                props[kvp.Key] = kvp.Value?.ToString() ?? "";
            }

            lock (parent.Events)
            {
                parent.Events.Add(new GateEvent(
                    EventName: eventName,
                    Timestamp: DateTimeOffset.UtcNow,
                    Properties: props));
            }
        }
    }
}

/// <summary>
/// Captured pipeline telemetry event. <see cref="Properties"/> mirrors the
/// structured fields the runtime emits (Stage, Handler, Producer, Result,
/// Duration, ArtifactId, Reason, etc.).
/// </summary>
internal sealed record GateEvent(
    string EventName,
    DateTimeOffset Timestamp,
    Dictionary<string, string> Properties);
