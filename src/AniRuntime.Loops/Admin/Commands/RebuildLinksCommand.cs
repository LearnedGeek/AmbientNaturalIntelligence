using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///rebuild-links</c> — Feature 37 retroactive memory-link build.
/// Heavy operation (O(N²) embedding scan). Runs on demand.
/// </summary>
public sealed class RebuildLinksCommand : IAdminCommand
{
    private readonly IMemoryMaintenance _maintenance;
    private readonly ILogger<RebuildLinksCommand> _log;

    public RebuildLinksCommand(
        IMemoryMaintenance maintenance,
        ILogger<RebuildLinksCommand> log)
    {
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _log         = log         ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "rebuild-links";
    public string HelpText => "rebuild-links — Build memory graph links (one-time, may take minutes)";

    public async Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
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
}
