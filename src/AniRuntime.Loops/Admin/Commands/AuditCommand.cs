using AniRuntime.Core.Interfaces;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///audit</c> — show the 5 most recent memory-audit-log entries.
/// </summary>
public sealed class AuditCommand : IAdminCommand
{
    private readonly IMemoryMaintenance _maintenance;

    public AuditCommand(IMemoryMaintenance maintenance)
    {
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
    }

    public string Name => "audit";
    public string HelpText => "audit — Show last 5 memory changes";

    public async Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
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
}
