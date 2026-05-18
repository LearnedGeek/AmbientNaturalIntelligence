using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Admin.Commands;

/// <summary>
/// <c>///diagnose</c> — run the diagnostic service and return a short
/// summary text. Detailed report goes to the log.
/// </summary>
public sealed class DiagnoseCommand : IAdminCommand
{
    private readonly IDiagnosticService _diagnostic;
    private readonly ILogger<DiagnoseCommand> _log;

    public DiagnoseCommand(
        IDiagnosticService diagnostic,
        ILogger<DiagnoseCommand> log)
    {
        _diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        _log        = log        ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "diagnose";
    public string HelpText => "diagnose — Run diagnostic service, summary returned via SMS";

    public async Task<string> ExecuteAsync(string trimmedInput, CancellationToken ct)
    {
        var report = await _diagnostic.RunDiagnosticAsync(ct).ConfigureAwait(false);
        _log.LogInformation("Admin: diagnose — {Summary}", report.Summary);
        return $"Diagnostic: {report.Summary}";
    }
}
