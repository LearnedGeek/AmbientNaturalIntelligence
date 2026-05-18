using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///test</c> — snapshot the DB to disk and enter test mode. Memories
/// created after this command are part of the test session and will be
/// purged when <c>///live</c> is run (DB restored from the snapshot).
/// </summary>
public sealed class TestModeOnCommand : IAdminCommand
{
    private readonly ITestModeTracker _tracker;
    private readonly AniOptions _options;
    private readonly ILogger<TestModeOnCommand> _log;

    public TestModeOnCommand(
        ITestModeTracker tracker,
        IOptions<AniOptions> options,
        ILogger<TestModeOnCommand> log)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _options = options.Value;
        _log     = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "test";
    public string HelpText => "test — Snapshot DB, enter test mode";

    public Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
    {
        if (_tracker.IsActive)
            return Task.FromResult($"Already in test mode (since {_tracker.StartedAt!.Value:HH:mm})");

        var dbPath = Path.Combine(AppContext.BaseDirectory, _options.MemoryDbPath);
        var snapshotPath = dbPath + ".test-snapshot";

        try
        {
            // SQLite WAL mode: copy .db, .db-shm, and .db-wal.
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
            return Task.FromResult($"ERROR: DB snapshot failed — {ex.Message}");
        }

        _tracker.Enable();
        _log.LogWarning("TEST MODE ENABLED at {Time}", _tracker.StartedAt);

        return Task.FromResult("Test mode ON. DB snapshot saved.\nMemories created from now will be purged on ///live.");
    }
}
