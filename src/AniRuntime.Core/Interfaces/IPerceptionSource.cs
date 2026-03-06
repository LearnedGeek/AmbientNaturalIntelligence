using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

public interface IPerceptionSource
{
    string             SourceName { get; }
    PerceptionCategory Category   { get; }
    bool               IsEnabled  { get; }

    Task<IEnumerable<PerceptionEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default);
}
