using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

public interface IAniAction
{
    /// <summary>Matches OutreachDecision.ActionType — use ActionTypes constants.</summary>
    string ActionType { get; }

    Task<bool> ExecuteAsync(OutreachDecision decision, CancellationToken ct = default);
}
