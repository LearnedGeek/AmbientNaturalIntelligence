using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///reset-mood</c> — reset the four emotional dimensions to their
/// personality baselines. Useful for clearing post-test state or
/// recovering from an extreme drift.
/// </summary>
public sealed class ResetMoodCommand : IAdminCommand
{
    private readonly IStateStore _state;
    private readonly IMemoryPersistence _persist;
    private readonly ILogger<ResetMoodCommand> _log;

    public ResetMoodCommand(
        IStateStore state,
        IMemoryPersistence persist,
        ILogger<ResetMoodCommand> log)
    {
        _state   = state   ?? throw new ArgumentNullException(nameof(state));
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "reset-mood";
    public string HelpText => "reset-mood — Reset emotions to baselines";

    public async Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
    {
        var emotional = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var before = $"W={emotional.Warmth:F2} E={emotional.Energy:F2} C={emotional.Worry:F2} P={emotional.Playfulness:F2}";

        emotional.Warmth      = emotional.WarmthBaseline;
        emotional.Energy      = emotional.EnergyBaseline;
        emotional.Worry       = emotional.WorryBaseline;
        emotional.Playfulness = emotional.PlayfulnessBaseline;
        emotional.LastUpdated = DateTimeOffset.UtcNow;

        await _persist.SaveEmotionalStateAsync(emotional, ct).ConfigureAwait(false);

        _log.LogWarning("Emotional state reset to baselines (was: {Before})", before);

        return $"Mood reset to baselines.\nBefore: {before}\nAfter: W={emotional.Warmth:F2} E={emotional.Energy:F2} C={emotional.Worry:F2} P={emotional.Playfulness:F2}";
    }
}
