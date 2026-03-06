using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Actions;

/// <summary>
/// Routes an OutreachDecision to the registered IAniAction implementation
/// whose ActionType matches. New output channels are added by registering
/// new IAniAction implementations in DI — no changes needed here.
/// </summary>
public class AniActionDispatcher
{
    private readonly IReadOnlyDictionary<string, IAniAction> _actions;
    private readonly ILogger<AniActionDispatcher>            _log;

    public AniActionDispatcher(IEnumerable<IAniAction> actions, ILogger<AniActionDispatcher> log)
    {
        _actions = actions.ToDictionary(a => a.ActionType, StringComparer.OrdinalIgnoreCase);
        _log     = log;
    }

    public async Task<bool> DispatchAsync(OutreachDecision decision, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(decision.ActionType))
        {
            _log.LogWarning("OutreachDecision has no ActionType — cannot dispatch");
            return false;
        }

        if (!_actions.TryGetValue(decision.ActionType, out var action))
        {
            _log.LogWarning("No registered action for type '{ActionType}'", decision.ActionType);
            return false;
        }

        _log.LogInformation("Dispatching {ActionType}: {Message}",
            decision.ActionType, decision.Message?[..Math.Min(80, decision.Message?.Length ?? 0)]);

        return await action.ExecuteAsync(decision, ct).ConfigureAwait(false);
    }
}
