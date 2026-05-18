using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///status</c> — emotional state, desire level, cooldown, recent
/// contact timing, active triggers, cycle timing.
/// </summary>
public sealed class StatusCommand : IAdminCommand
{
    private readonly IStateStore _state;
    private readonly DesireEngine _desire;
    private readonly ITestModeTracker _testMode;
    private readonly AniOptions _options;

    public StatusCommand(
        IStateStore state,
        DesireEngine desire,
        ITestModeTracker testMode,
        IOptions<AniOptions> options)
    {
        _state    = state    ?? throw new ArgumentNullException(nameof(state));
        _desire   = desire   ?? throw new ArgumentNullException(nameof(desire));
        _testMode = testMode ?? throw new ArgumentNullException(nameof(testMode));
        _options  = options.Value;
    }

    public string Name => "status";
    public string HelpText => "status — Emotional state, desire, timing";

    public async Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
    {
        var emotional = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var desire    = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        var charState = await _state.GetCharacterStateAsync(ct).ConfigureAwait(false);

        var moodDesc = emotional.Describe();
        var mood = string.IsNullOrEmpty(moodDesc) ? "baseline" : moodDesc;

        var lines = new List<string>
        {
            $"=== {charState.Name} Status ===",
            $"Mode: {(_testMode.IsActive ? "TEST" : "LIVE")}",
            $"Mood: {mood}",
            $"  W={emotional.Warmth:F2} E={emotional.Energy:F2} C={emotional.Worry:F2} P={emotional.Playfulness:F2}",
            $"Desire: {desire.DesireToConnect:F2} (threshold: {_options.OutreachThresholdFloor:F2}–{_options.OutreachThresholdFloor + _options.OutreachThresholdRange:F2})",
            $"Cooldown: {(desire.CooldownActive ? $"until {desire.CooldownUntil:HH:mm}" : "none")}",
            $"Last outreach: {FormatAge(desire.LastOutreach)}",
            $"Last contact: {FormatAge(desire.LastContactInbound)}",
            $"Active triggers: {desire.ActiveTriggers.Count}",
            $"Cycle timing: {_options.MinWakeMinutes:F0}–{_options.MaxWakeMinutes:F0} min (conversation: {_options.ConversationHeartbeatSeconds:F0}s)",
        };

        if (_testMode.IsActive)
            lines.Add($"Test started: {_testMode.StartedAt!.Value:HH:mm}");

        return string.Join("\n", lines);
    }

    private static string FormatAge(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp;
        if (age.TotalMinutes < 1)  return "just now";
        if (age.TotalMinutes < 60) return $"{age.TotalMinutes:F0}m ago";
        if (age.TotalHours   < 24) return $"{age.TotalHours:F1}h ago";
        return $"{age.TotalDays:F0}d ago";
    }
}
