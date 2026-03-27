using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Feature 41: Scheduled diagnostic runner.
/// Runs the diagnostic service every N minutes and logs findings.
/// Only logs at Warning+ to avoid noise — Healthy scans are Debug level.
/// </summary>
public class DiagnosticScheduler : BackgroundService
{
    private readonly IDiagnosticService _diagnostic;
    private readonly DiagnosticOptions _options;
    private readonly ILogger<DiagnosticScheduler> _log;

    public DiagnosticScheduler(
        IDiagnosticService diagnostic,
        IOptions<DiagnosticOptions> options,
        ILogger<DiagnosticScheduler> log)
    {
        _diagnostic = diagnostic;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Diagnostic scheduler disabled");
            return;
        }

        _log.LogInformation("Diagnostic scheduler started — scanning every {Interval} minutes",
            _options.IntervalMinutes);

        // Initial delay to let the system warm up
        await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var report = await _diagnostic.RunDiagnosticAsync(ct).ConfigureAwait(false);

                if (report.OverallSeverity >= DiagnosticSeverity.Warning)
                {
                    _log.LogWarning("Diagnostic: {Summary}", report.Summary);
                }
                else if (report.Findings.Count > 0)
                {
                    _log.LogInformation("Diagnostic: {Severity} — {Count} info findings ({Lines} lines scanned)",
                        report.OverallSeverity, report.Findings.Count, report.LinesScanned);
                }
                else
                {
                    _log.LogDebug("Diagnostic: Healthy ({Lines} lines, {Window:F0} min window)",
                        report.LinesScanned, report.WindowScanned.TotalMinutes);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Diagnostic scan cycle failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(_options.IntervalMinutes), ct).ConfigureAwait(false);
        }
    }
}
