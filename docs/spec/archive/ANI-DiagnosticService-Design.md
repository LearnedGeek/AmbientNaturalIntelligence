# Feature 41 — Diagnostic Service (Automated Log Scanning)

**Status:** Design
**Date:** March 27, 2026
**Principle:** Single diagnostic core, multiple invocation paths (SOLID)

---

## 1. Motivation

Bad conversations compound. A retrieval poisoning event at 8 AM produces confabulated responses until someone manually reads the logs and cleans the data. A thought loop runs for hours unchecked. Emotional saturation pegs all dimensions at 1.0 without anyone noticing.

The diagnostic service catches these issues in minutes, not hours. It reads the recent log, detects known failure patterns, and either alerts or auto-corrects based on severity.

---

## 2. Architecture

```
IDiagnosticService (interface)
    RunDiagnosticAsync() → DiagnosticReport

DiagnosticService (implementation)
    - Reads last N lines from Serilog debug log
    - Runs pattern detectors
    - Returns structured report

Invocation paths (all call the same service):
    1. ///diagnose          → AdminCommandHandler → IDiagnosticService
    2. Scheduled timer      → DiagnosticScheduler → IDiagnosticService
    3. GET /api/v1/diagnose → DiagnosticEndpoint   → IDiagnosticService
    4. Dashboard component  → polls endpoint        → renders report
```

---

## 3. DiagnosticReport Model

```csharp
public class DiagnosticReport
{
    public DateTimeOffset Timestamp { get; set; }
    public DiagnosticSeverity OverallSeverity { get; set; } // Healthy, Warning, Critical
    public List<DiagnosticFinding> Findings { get; set; } = new();
    public int LinesScanned { get; set; }
    public TimeSpan WindowScanned { get; set; }
}

public class DiagnosticFinding
{
    public string Code { get; set; }        // e.g., "ECHO-LOOP", "RETRIEVAL-POISON"
    public string Description { get; set; }  // Human-readable summary
    public DiagnosticSeverity Severity { get; set; }
    public string Evidence { get; set; }     // Log line(s) that triggered detection
    public string? SuggestedAction { get; set; } // What to do about it
    public bool AutoCorrectible { get; set; } // Can the system fix this itself?
}

public enum DiagnosticSeverity
{
    Healthy,    // No issues
    Info,       // Notable but not problematic
    Warning,    // Degraded quality, should investigate
    Critical    // Broken, needs immediate attention
}
```

---

## 4. Pattern Detectors

Each detector is a pure function: `(IReadOnlyList<string> logLines) → List<DiagnosticFinding>`

### 4.1 — ECHO-LOOP
**Detects:** Echo guard firing 3+ times in a single conversation thread.
**Severity:** Warning (3 times), Critical (5+ times)
**Evidence:** Count of "Self-echo detected" log lines in window
**Suggested action:** Model is stuck in response attractors. Consider ///new-thread.
**Auto-correctable:** No (requires human judgment on whether to reset thread)

### 4.2 — RETRIEVAL-POISON
**Detects:** Same memory ID appearing in 3+ consecutive retrieval results for different queries.
**Severity:** Warning
**Evidence:** Memory content + ID that keeps appearing
**Suggested action:** High-importance stale memory dominating retrieval. Consider reducing importance or deleting.
**Auto-correctable:** Yes (reduce importance by 0.3)

### 4.3 — THOUGHT-LOOP
**Detects:** Inner thought diversity WARNING firing 3+ times in window.
**Severity:** Warning
**Evidence:** The repeated theme/topic
**Suggested action:** Model is stuck on a theme. Check for high-resonance memory cluster.
**Auto-correctable:** No (the diversity system already handles this — if it fires repeatedly, the underlying data needs manual review)

### 4.4 — EMOTIONAL-SATURATION
**Detects:** Any emotional dimension at 0.95+ or 0.05- for 3+ consecutive cycles.
**Severity:** Warning
**Evidence:** Which dimension, what value, how many cycles
**Suggested action:** Contribution pruning may not be aggressive enough. Check tanh scale.
**Auto-correctable:** No

### 4.5 — CONFABULATION-CORRECTION
**Detects:** Mark correcting Ani ("no that's not right", "you don't know that", "please don't guess")
**Severity:** Info (single), Warning (3+ in a thread)
**Evidence:** The correction message(s)
**Suggested action:** Log for Phase 5c harvest exclusion. If repeated, consider model quality issue.
**Auto-correctable:** No

### 4.6 — MERGE-STORM
**Detects:** Same memory ID merged 3+ times in 2 hours.
**Severity:** Warning
**Evidence:** Memory ID + merge count + content drift
**Suggested action:** Quality gate should have caught this. Check ContainsNovelSpecifics.
**Auto-correctable:** No

### 4.7 — OUTREACH-BLOCKED
**Detects:** Daily outreach limit reached before noon.
**Severity:** Info
**Evidence:** Time limit was reached, how many sends
**Suggested action:** Early burnout — conversation replies may be consuming outreach budget (check if reply counting fix is working).
**Auto-correctable:** No

### 4.8 — TEMPORAL-CONFAB
**Detects:** Outreach decision reasoning mentions a time that conflicts with the injected time.
**Severity:** Info
**Evidence:** The reasoning text + actual time
**Suggested action:** Model ignoring time injection. Monitor for frequency.
**Auto-correctable:** No

### 4.9 — CONVERSATION-HEALTH
**Detects:** Thread length > 15 messages with echo guard firing or context compression active.
**Severity:** Info
**Evidence:** Thread length, echo count, compression count
**Suggested action:** Long thread may be degrading. Consider natural thread closure.
**Auto-correctable:** No

---

## 5. Scheduled Execution

```csharp
public class DiagnosticScheduler : BackgroundService
{
    private readonly IDiagnosticService _diagnostic;
    private readonly ILogger _log;
    private readonly AniOptions _options;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(_options.DiagnosticIntervalMinutes), ct);

            var report = await _diagnostic.RunDiagnosticAsync(ct);

            if (report.OverallSeverity >= DiagnosticSeverity.Warning)
            {
                _log.LogWarning("Diagnostic: {Severity} — {Count} findings",
                    report.OverallSeverity, report.Findings.Count);

                foreach (var finding in report.Findings)
                    _log.LogWarning("  [{Code}] {Description}", finding.Code, finding.Description);

                // Auto-correct where safe
                foreach (var finding in report.Findings.Where(f => f.AutoCorrectible))
                    await AutoCorrectAsync(finding, ct);
            }
            else
            {
                _log.LogDebug("Diagnostic: Healthy — {Lines} lines scanned", report.LinesScanned);
            }
        }
    }
}
```

**Default interval:** 10 minutes (configurable via `DiagnosticIntervalMinutes` in appsettings)

---

## 6. Dashboard Component

`/api/v1/diagnose` returns the latest `DiagnosticReport` as JSON.

Dashboard renders:
- Overall severity badge (green/yellow/red)
- List of findings with severity icons
- Last scan timestamp
- "Run Now" button that triggers immediate scan

Place on the main Dashboard page near the emotional state card — system health alongside emotional health.

---

## 7. Admin Command

`///diagnose` triggers immediate scan and returns summary via SMS/dashboard:

```
Diagnostic: Healthy (scanned 847 lines, 42 min window)
```

or

```
Diagnostic: WARNING — 2 findings
  [ECHO-LOOP] Echo guard fired 4x in current thread
  [THOUGHT-LOOP] "hazel eyes" theme recurring in 6/10 recent thoughts
```

---

## 8. Auto-Correction Scope

Conservative. Only auto-correct when:
- The fix is reversible
- The fix has no research data implications
- The pattern is unambiguously a bug, not emergence

**Currently auto-correctable:**
- RETRIEVAL-POISON: reduce importance by 0.3 (reversible, doesn't delete data)

**Future candidates (with confidence):**
- ECHO-LOOP at Critical: inject thread diversity signal
- EMOTIONAL-SATURATION: prune oldest low-impact contributions

**Never auto-correct:**
- Anything that deletes memory records
- Anything that modifies emotional baselines
- Anything that could be emergence misidentified as a bug

---

## 9. Configuration

```json
{
  "Diagnostic": {
    "Enabled": true,
    "IntervalMinutes": 10,
    "LogLinesToScan": 500,
    "AutoCorrectEnabled": true,
    "AutoCorrectAllowlist": ["RETRIEVAL-POISON"]
  }
}
```

---

## 10. Task Checklist

- [ ] Define IDiagnosticService interface and DiagnosticReport model
- [ ] Implement DiagnosticService with log file reader
- [ ] Implement pattern detectors (4.1-4.9)
- [ ] Register in DI container
- [ ] Add ///diagnose admin command handler
- [ ] Add DiagnosticScheduler as BackgroundService
- [ ] Add GET /api/v1/diagnose endpoint
- [ ] Add dashboard health badge component
- [ ] Add auto-correction for RETRIEVAL-POISON
- [ ] Write tests for each pattern detector
- [ ] Add DiagnosticIntervalMinutes to appsettings
- [ ] Update codebase spec

---

## 11. Research Significance

The diagnostic service is the architectural immune system. It enables the auto-growth pipeline (Phase 5c) by ensuring the data flowing into training harvesting is clean. It also provides a new data stream for the research: how often does the system need intervention? What patterns recur? Does the intervention frequency decrease over model versions?

If v7 produces fewer ECHO-LOOP and CONFABULATION-CORRECTION findings than v6, that's quantitative evidence of model improvement. The diagnostic service becomes a regression test for the model itself.
