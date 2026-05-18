using AniRuntime.Core.Interfaces;
using AniRuntime.Emergence;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///rebuild-emergence</c> — backfill EM1–EM6 emergence-type tags on
/// historical log entries. Used after the emergence taxonomy evolves
/// (new EM type added) to retag the older entries.
/// </summary>
public sealed class RebuildEmergenceCommand : IAdminCommand
{
    private readonly EmergenceStore _emergence;
    private readonly ILogger<RebuildEmergenceCommand> _log;

    public RebuildEmergenceCommand(
        EmergenceStore emergence,
        ILogger<RebuildEmergenceCommand> log)
    {
        _emergence = emergence ?? throw new ArgumentNullException(nameof(emergence));
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "rebuild-emergence";
    public string HelpText => "rebuild-emergence — Tag historical emergence log with EM1-EM6 types";

    public async Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
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
}
