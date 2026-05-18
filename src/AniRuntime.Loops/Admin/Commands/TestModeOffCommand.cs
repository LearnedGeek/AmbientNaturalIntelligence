using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///live</c> — restore the DB from the test-snapshot and exit test
/// mode. All memories created during the test session are discarded.
/// </summary>
public sealed class TestModeOffCommand : IAdminCommand
{
    private readonly ITestModeTracker _tracker;
    private readonly AniOptions _options;
    private readonly ILogger<TestModeOffCommand> _log;

    public TestModeOffCommand(
        ITestModeTracker tracker,
        IOptions<AniOptions> options,
        ILogger<TestModeOffCommand> log)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _options = options.Value;
        _log     = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "live";
    public string HelpText => "live — Restore DB, exit test mode";

    public Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
    {
        if (!_tracker.IsActive)
            return Task.FromResult("Not in test mode. Send ///test first.");

        var startedAt = _tracker.Disable() ?? DateTimeOffset.UtcNow;

        var dbPath = Path.Combine(AppContext.BaseDirectory, _options.MemoryDbPath);
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
                    File.Delete(src);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to restore DB from snapshot");
            return Task.FromResult($"ERROR: DB restore failed — {ex.Message}. Snapshot files may still exist at {snapshotPath}");
        }

        _log.LogWarning("TEST MODE DISABLED — DB restored from snapshot (test started {StartedAt:HH:mm})", startedAt);

        return Task.FromResult($"Live mode restored. DB rolled back to pre-test state.\nTest session lasted {(DateTimeOffset.UtcNow - startedAt).TotalMinutes:F0} minutes.");
    }
}
