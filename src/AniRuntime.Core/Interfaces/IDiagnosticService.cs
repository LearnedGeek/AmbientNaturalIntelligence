namespace AniRuntime.Core.Interfaces;

public interface IDiagnosticService
{
    Task<DiagnosticReport> RunDiagnosticAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent diagnostic report without triggering a new scan.
    /// Used by the cognitive cycle to check for active findings (e.g., PERCEPTION-ANCHOR).
    /// </summary>
    DiagnosticReport? LatestReport { get; }
}

public class DiagnosticReport
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public DiagnosticSeverity OverallSeverity { get; set; } = DiagnosticSeverity.Healthy;
    public List<DiagnosticFinding> Findings { get; set; } = new();
    public int LinesScanned { get; set; }
    public TimeSpan WindowScanned { get; set; }

    public string Summary => OverallSeverity == DiagnosticSeverity.Healthy
        ? $"Healthy (scanned {LinesScanned} lines, {WindowScanned.TotalMinutes:F0} min window)"
        : $"{OverallSeverity} — {Findings.Count} finding{(Findings.Count == 1 ? "" : "s")}\n" +
          string.Join("\n", Findings.Select(f => $"  [{f.Code}] {f.Description}"));
}

public class DiagnosticFinding
{
    public required string Code { get; set; }
    public required string Description { get; set; }
    public DiagnosticSeverity Severity { get; set; }
    public string? Evidence { get; set; }
    public string? SuggestedAction { get; set; }
    public bool AutoCorrectible { get; set; }
}

public enum DiagnosticSeverity
{
    Healthy,
    Info,
    Warning,
    Critical
}
