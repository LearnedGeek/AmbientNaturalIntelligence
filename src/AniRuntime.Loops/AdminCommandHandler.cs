using System.Text.Json;
using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.Emergence;
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
public class AdminCommandHandler : IAdminCommandHandler
{
    private readonly IStateStore          _state;
    private readonly IMemoryPersistence  _persist;
    private readonly IMemoryMaintenance  _maintenance;
    private readonly IConversationService _conversations;
    private readonly DesireEngine        _desire;
    private readonly AniActionDispatcher _dispatcher;
    private readonly EmergenceStore      _emergence;
    private readonly IDiagnosticService  _diagnostic;
    private readonly AniOptions          _aniOptions;
    private readonly ILogger<AdminCommandHandler> _log;

    private DateTimeOffset? _testModeStartedAt;

    public bool IsTestMode => _testModeStartedAt.HasValue;

    public AdminCommandHandler(
        IStateStore                     state,
        IMemoryPersistence              persist,
        IMemoryMaintenance              maintenance,
        IConversationService            conversations,
        DesireEngine                    desire,
        AniActionDispatcher             dispatcher,
        EmergenceStore                  emergence,
        IDiagnosticService              diagnostic,
        IOptions<AniOptions>            aniOptions,
        ILogger<AdminCommandHandler>    log)
    {
        _state         = state;
        _persist       = persist;
        _maintenance   = maintenance;
        _conversations = conversations;
        _desire        = desire;
        _dispatcher    = dispatcher;
        _emergence     = emergence;
        _diagnostic    = diagnostic;
        _aniOptions    = aniOptions.Value;
        _log           = log;
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
            "new-thread" => await HandleNewThreadAsync(ct).ConfigureAwait(false),
            "rebuild-links" => await HandleRebuildLinksAsync(ct).ConfigureAwait(false),
            "rebuild-emergence" => await HandleRebuildEmergenceAsync(ct).ConfigureAwait(false),
            "diagnose" => await HandleDiagnoseAsync(ct).ConfigureAwait(false),
            _ when trimmed.StartsWith("tag ") => await HandleTagAsync(trimmed[4..].Trim(), ct).ConfigureAwait(false),
            "audit" => await HandleAuditAsync(ct).ConfigureAwait(false),
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
            "///new-thread — Close current thread, start fresh",
            "///rebuild-links — Build memory graph links (one-time, may take minutes)",
            "///rebuild-emergence — Tag historical emergence log with EM1-EM6 types",
            "///tag <label> — Tag the last reply with a label (e.g. confabulation, temporal",
            "              confusion, repetition) — saved to confabulation_flags table",
            "///audit       — Show last 5 memory changes",
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

    /// <summary>
    /// AC5: Tag the most recent Ani reply with a label and persist it for pattern analysis.
    /// Consolidated Apr 22, 2026 — previously split between ///tag (text-file only) and
    /// ///flag (DB-only, no label). Now a single path: ///tag &lt;label&gt; saves the
    /// preceding Mark message + Ani's reply to the confabulation_flags table with the
    /// label as `topic_category`. Common labels Mark uses: "confabulation", "temporal
    /// confusion", "repetition", and other ad-hoc research tags.
    /// </summary>
    private async Task<string> HandleTagAsync(string note, CancellationToken ct)
    {
        _log.LogInformation("[TAG] {Note}", note);

        if (string.IsNullOrWhiteSpace(note))
            return "Usage: ///tag <label>  — e.g. ///tag confabulation";

        // Find the most recent conversation thread with Ani's reply
        var thread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
        if (thread is null || thread.Messages.Count < 2)
        {
            var recent = await _conversations.GetRecentThreadsAsync(1, ct).ConfigureAwait(false);
            thread = recent.FirstOrDefault();
        }

        if (thread is null || thread.Messages.Count < 2)
            return $"Tagged [{note}] — but no recent conversation pair found to persist.";

        // Find the last Ani reply and the preceding Mark message
        string? aniReply    = null;
        string? markMessage = null;

        for (var i = thread.Messages.Count - 1; i >= 0; i--)
        {
            if (aniReply is null && thread.Messages[i].Role == Roles.Ani)
                aniReply = thread.Messages[i].Content;
            else if (aniReply is not null && thread.Messages[i].Role == Roles.Mark)
            {
                markMessage = thread.Messages[i].Content;
                break;
            }
        }

        if (aniReply is null)
            return $"Tagged [{note}] — no Ani reply found in recent conversation.";

        markMessage ??= "(no preceding message found)";

        await _persist.SaveConfabulationFlagAsync(
            markMessage, aniReply, topicCategory: note, notes: null, ct: ct).ConfigureAwait(false);

        return $"Tagged [{note}]:\n→ \"{markMessage[..Math.Min(60, markMessage.Length)]}\"\n← \"{aniReply[..Math.Min(60, aniReply.Length)]}\"";
    }

    private Task<string> HandleNewThreadAsync(CancellationToken ct)
    {
        // The cognitive cycle already closes the thread before calling HandleAsync
        // (CognitiveCycleProcessor line 152), so by the time we get here the thread
        // is already closed. Just confirm it to the user.
        _log.LogInformation("Admin: new-thread — current thread closed by cycle processor");
        return Task.FromResult("Thread closed. Next message will start a fresh thread.");
    }

    private async Task<string> HandleRebuildLinksAsync(CancellationToken ct)
    {
        try
        {
            var (mergeCount, linkCount) = await _maintenance.RebuildMemoryLinksAsync(ct).ConfigureAwait(false);
            return $"Memory graph rebuilt.\n{linkCount} links created.\n{mergeCount} duplicates detected (logged for review).";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rebuild links failed");
            return $"ERROR: Rebuild failed — {ex.Message}";
        }
    }

    private async Task<string> HandleRebuildEmergenceAsync(CancellationToken ct)
    {
        try
        {
            _log.LogInformation("Admin: rebuild-emergence — backfilling emergence types on historical log entries");
            var tagged = await _emergence.BackfillEmergenceTypesAsync(ct).ConfigureAwait(false);
            _log.LogInformation("Admin: rebuild-emergence complete — {Count} entries tagged", tagged);
            return $"Emergence taxonomy backfill complete.\n{tagged} entries tagged with EM1-EM6 types.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rebuild emergence failed");
            return $"ERROR: Rebuild emergence failed — {ex.Message}";
        }
    }

    private async Task<string> HandleAuditAsync(CancellationToken ct)
    {
        try
        {
            var recent = await _maintenance.GetRecentAuditEntriesAsync(5, ct).ConfigureAwait(false);
            if (recent.Count == 0)
                return "No audit entries yet.";

            var lines = new List<string> { $"=== Last {recent.Count} memory changes ===" };
            foreach (var entry in recent)
            {
                var preview = entry.ContentBefore ?? entry.ContentAfter ?? "(no content)";
                if (preview.Length > 50) preview = preview[..50] + "...";
                lines.Add($"{entry.OccurredAt:HH:mm} {entry.Action} ({entry.Source}): {preview}");
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Audit query failed: {ex.Message}";
        }
    }

    private async Task<string> HandleDiagnoseAsync(CancellationToken ct)
    {
        var report = await _diagnostic.RunDiagnosticAsync(ct).ConfigureAwait(false);
        _log.LogInformation("Admin: diagnose — {Summary}", report.Summary);
        return $"Diagnostic: {report.Summary}";
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
