namespace AniRuntime.Core;

/// <summary>
/// Configuration for the diagnostic service (Feature 41).
/// Bound from appsettings.json "Diagnostic" section.
/// </summary>
public class DiagnosticOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 10;
    public int LogLinesToScan { get; set; } = 500;
    public bool AutoCorrectEnabled { get; set; } = true;
    public List<string> AutoCorrectAllowlist { get; set; } = new() { "RETRIEVAL-POISON" };
}
