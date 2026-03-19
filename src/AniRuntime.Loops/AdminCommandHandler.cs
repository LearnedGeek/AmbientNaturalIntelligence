using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Handles admin commands sent via SMS with the "///" prefix.
/// These bypass the conversation pipeline entirely and execute system operations.
///
/// Commands:
///   ///test   — Snapshot the DB and enter test mode (new memories are tagged)
///   ///live   — Purge test-mode memories and return to live operation
///   ///status — Reply with current emotional state, desire level, memory stats
/// </summary>
public class AdminCommandHandler
{
    private readonly IStateStore          _state;
    private readonly IMemoryPersistence  _persist;
    private readonly DesireEngine        _desire;
    private readonly AniActionDispatcher _dispatcher;
    private readonly AniOptions          _aniOptions;
    private readonly ILogger<AdminCommandHandler> _log;

    private DateTimeOffset? _testModeStartedAt;

    public bool IsTestMode => _testModeStartedAt.HasValue;

    public AdminCommandHandler(
        IStateStore                     state,
        IMemoryPersistence              persist,
        DesireEngine                    desire,
        AniActionDispatcher             dispatcher,
        IOptions<AniOptions>            aniOptions,
        ILogger<AdminCommandHandler>    log)
    {
        _state      = state;
        _persist    = persist;
        _desire     = desire;
        _dispatcher = dispatcher;
        _aniOptions = aniOptions.Value;
        _log        = log;
    }

    /// <summary>
    /// Returns true if the message is an admin command (starts with "///").
    /// </summary>
    public static bool IsAdminCommand(string message)
        => message.TrimStart().StartsWith("///");

    /// <summary>
    /// Parses and executes the admin command, sending an SMS reply with the result.
    /// Returns true if handled, false if the command was unrecognized.
    /// </summary>
    public async Task<bool> HandleAsync(string message, CancellationToken ct)
    {
        var trimmed = message.TrimStart()[3..].Trim().ToLowerInvariant(); // strip "///"
        _log.LogInformation("Admin command received: ///{Command}", trimmed);

        var reply = trimmed switch
        {
            "help"       => HandleHelp(),
            "test"       => await HandleTestAsync(ct).ConfigureAwait(false),
            "live"       => await HandleLiveAsync(ct).ConfigureAwait(false),
            "status"     => await HandleStatusAsync(ct).ConfigureAwait(false),
            "reset-mood" => await HandleResetMoodAsync(ct).ConfigureAwait(false),
            _            => $"Unknown command: ///{trimmed}\nSend ///help for available commands."
        };

        await SendAdminReplyAsync(reply, ct).ConfigureAwait(false);
        return true;
    }

    private static string HandleHelp()
    {
        return string.Join("\n", new[]
        {
            "=== Admin Commands ===",
            "///help       — Show this list",
            "///status     — Emotional state, desire, timing",
            "///test       — Snapshot DB, enter test mode",
            "///live       — Restore DB, exit test mode",
            "///reset-mood — Reset emotions to baselines",
        });
    }

    private async Task<string> HandleTestAsync(CancellationToken ct)
    {
        if (_testModeStartedAt.HasValue)
            return $"Already in test mode (since {_testModeStartedAt.Value:HH:mm})";

        // Snapshot: copy the SQLite DB files
        var dbPath = Path.Combine(AppContext.BaseDirectory, _aniOptions.MemoryDbPath);
        var snapshotPath = dbPath + ".test-snapshot";

        try
        {
            // SQLite WAL mode: need to copy .db, .db-shm, and .db-wal
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                var src = dbPath + suffix;
                var dst = snapshotPath + suffix;
                if (File.Exists(src))
                    File.Copy(src, dst, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to snapshot DB for test mode");
            return $"ERROR: DB snapshot failed — {ex.Message}";
        }

        _testModeStartedAt = DateTimeOffset.UtcNow;
        _log.LogWarning("TEST MODE ENABLED at {Time}", _testModeStartedAt);

        return $"Test mode ON. DB snapshot saved.\nMemories created from now will be purged on ///live.";
    }

    private async Task<string> HandleLiveAsync(CancellationToken ct)
    {
        if (!_testModeStartedAt.HasValue)
            return "Not in test mode. Send ///test first.";

        var startedAt = _testModeStartedAt.Value;
        _testModeStartedAt = null;

        // Restore: overwrite DB from snapshot
        var dbPath = Path.Combine(AppContext.BaseDirectory, _aniOptions.MemoryDbPath);
        var snapshotPath = dbPath + ".test-snapshot";

        try
        {
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                var src = snapshotPath + suffix;
                var dst = dbPath + suffix;
                if (File.Exists(src))
                {
                    File.Copy(src, dst, overwrite: true);
                    File.Delete(src); // clean up snapshot
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to restore DB from snapshot");
            return $"ERROR: DB restore failed — {ex.Message}. Snapshot files may still exist at {snapshotPath}";
        }

        _log.LogWarning("TEST MODE DISABLED — DB restored from snapshot (test started {StartedAt:HH:mm})", startedAt);

        return $"Live mode restored. DB rolled back to pre-test state.\nTest session lasted {(DateTimeOffset.UtcNow - startedAt).TotalMinutes:F0} minutes.";
    }

    private async Task<string> HandleResetMoodAsync(CancellationToken ct)
    {
        var emotional = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var before = $"W={emotional.Warmth:F2} E={emotional.Energy:F2} C={emotional.Worry:F2} P={emotional.Playfulness:F2}";

        emotional.Warmth      = emotional.WarmthBaseline;
        emotional.Energy      = emotional.EnergyBaseline;
        emotional.Worry     = emotional.WorryBaseline;
        emotional.Playfulness = emotional.PlayfulnessBaseline;
        emotional.LastUpdated = DateTimeOffset.UtcNow;

        await _persist.SaveEmotionalStateAsync(emotional, ct).ConfigureAwait(false);

        _log.LogWarning("Emotional state reset to baselines (was: {Before})", before);

        return $"Mood reset to baselines.\nBefore: {before}\nAfter: W={emotional.Warmth:F2} E={emotional.Energy:F2} C={emotional.Worry:F2} P={emotional.Playfulness:F2}";
    }

    private async Task<string> HandleStatusAsync(CancellationToken ct)
    {
        var emotional = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var desire    = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        var charState = await _state.GetCharacterStateAsync(ct).ConfigureAwait(false);

        var moodDesc = emotional.Describe();
        var mood = string.IsNullOrEmpty(moodDesc) ? "baseline" : moodDesc;

        var lines = new List<string>
        {
            $"=== {charState.Name} Status ===",
            $"Mode: {(IsTestMode ? "TEST" : "LIVE")}",
            $"Mood: {mood}",
            $"  W={emotional.Warmth:F2} E={emotional.Energy:F2} C={emotional.Worry:F2} P={emotional.Playfulness:F2}",
            $"Desire: {desire.DesireToConnect:F2} (threshold: {_aniOptions.OutreachThresholdFloor:F2}–{_aniOptions.OutreachThresholdFloor + _aniOptions.OutreachThresholdRange:F2})",
            $"Cooldown: {(desire.CooldownActive ? $"until {desire.CooldownUntil:HH:mm}" : "none")}",
            $"Last outreach: {FormatAge(desire.LastOutreach)}",
            $"Last contact: {FormatAge(desire.LastContactInbound)}",
            $"Active triggers: {desire.ActiveTriggers.Count}",
            $"Cycle timing: {_aniOptions.MinWakeMinutes:F0}–{_aniOptions.MaxWakeMinutes:F0} min (conversation: {_aniOptions.ConversationHeartbeatSeconds:F0}s)",
        };

        if (IsTestMode)
            lines.Add($"Test started: {_testModeStartedAt!.Value:HH:mm}");

        return string.Join("\n", lines);
    }

    private async Task SendAdminReplyAsync(string text, CancellationToken ct)
    {
        var decision = new OutreachDecision
        {
            ShouldReach = true,
            Message     = text,
            ActionType  = ActionTypes.Sms,
            Reasoning   = "admin command response",
        };

        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);
    }

    private static string FormatAge(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalMinutes < 60) return $"{age.TotalMinutes:F0}m ago";
        if (age.TotalHours < 24) return $"{age.TotalHours:F1}h ago";
        return $"{age.TotalDays:F0}d ago";
    }
}
