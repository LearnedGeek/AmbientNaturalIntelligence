using System.Text.RegularExpressions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Feature 41: Automated diagnostic service — the architectural immune system.
/// Reads recent log lines and detects known failure patterns.
/// Single core service, invocable via ///diagnose, scheduled timer, or API endpoint.
/// </summary>
public class DiagnosticService : IDiagnosticService
{
    private readonly DiagnosticOptions _options;
    private readonly ILogger<DiagnosticService> _log;
    private readonly string _logDirectory;

    /// <summary>
    /// Latest diagnostic report — available for other services to check.
    /// Thread-safe: written by DiagnosticScheduler, read by ContextBuilder.
    /// </summary>
    public DiagnosticReport? LatestReport { get; private set; }

    public DiagnosticService(
        IOptions<DiagnosticOptions> options,
        ILogger<DiagnosticService> log,
        IOptions<AniOptions> aniOptions)
    {
        _options = options.Value;
        _log = log;
        // Log files are in the service's data directory or working directory
        _logDirectory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "logs");
        if (!Directory.Exists(_logDirectory))
            _logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
    }

    public async Task<DiagnosticReport> RunDiagnosticAsync(CancellationToken ct = default)
    {
        var report = new DiagnosticReport();

        try
        {
            var lines = await ReadRecentLogLinesAsync(_options.LogLinesToScan, ct)
                .ConfigureAwait(false);

            report.LinesScanned = lines.Count;
            report.WindowScanned = EstimateTimeWindow(lines);

            // Run all pattern detectors
            report.Findings.AddRange(DetectEchoLoop(lines));
            report.Findings.AddRange(DetectRetrievalPoison(lines));
            report.Findings.AddRange(DetectThoughtLoop(lines));
            report.Findings.AddRange(DetectEmotionalSaturation(lines));
            report.Findings.AddRange(DetectConfabulationCorrection(lines));
            report.Findings.AddRange(DetectMergeStorm(lines));
            report.Findings.AddRange(DetectOutreachBlocked(lines));
            report.Findings.AddRange(DetectTemporalConfab(lines));
            report.Findings.AddRange(DetectConversationHealth(lines));
            report.Findings.AddRange(DetectPerceptionAnchor(lines));

            // Overall severity = max severity across findings
            report.OverallSeverity = report.Findings.Count == 0
                ? DiagnosticSeverity.Healthy
                : report.Findings.Max(f => f.Severity);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Diagnostic scan failed");
            report.Findings.Add(new DiagnosticFinding
            {
                Code = "SCAN-FAILED",
                Description = $"Diagnostic scan failed: {ex.Message}",
                Severity = DiagnosticSeverity.Warning,
            });
            report.OverallSeverity = DiagnosticSeverity.Warning;
        }

        LatestReport = report;
        return report;
    }

    // ── Pattern Detectors ─────────────────────────────────────────────────

    /// <summary>4.1 — Echo guard firing repeatedly in a thread.</summary>
    internal static List<DiagnosticFinding> DetectEchoLoop(IReadOnlyList<string> lines)
    {
        var echoCount = lines.Count(l => l.Contains("Self-echo detected"));
        if (echoCount < 3) return [];

        var severity = echoCount >= 5 ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning;
        return [new DiagnosticFinding
        {
            Code = "ECHO-LOOP",
            Description = $"Echo guard fired {echoCount}x — model stuck in response attractors",
            Severity = severity,
            Evidence = $"{echoCount} Self-echo detected lines in scan window",
            SuggestedAction = "Model converging on same response. Consider ///new-thread if conversation is degraded.",
        }];
    }

    /// <summary>4.2 — Same memory dominating retrieval across different queries.</summary>
    internal static List<DiagnosticFinding> DetectRetrievalPoison(IReadOnlyList<string> lines)
    {
        // Extract memory content snippets from "Diversity re-rank:" lines
        var rerankLines = lines.Where(l => l.Contains("Diversity re-rank:")).ToList();
        if (rerankLines.Count < 3) return [];

        // Count how many times each memory snippet appears across different re-ranks
        var snippetCounts = new Dictionary<string, int>();
        foreach (var line in rerankLines)
        {
            // Extract first memory snippet from re-rank line (format: "topic=score, topic=score")
            var match = Regex.Match(line, @"Diversity re-rank: (.{20,50})=");
            if (match.Success)
            {
                var snippet = match.Groups[1].Value.Trim();
                snippetCounts[snippet] = snippetCounts.GetValueOrDefault(snippet) + 1;
            }
        }

        var poisoned = snippetCounts.Where(kv => kv.Value >= 3).ToList();
        if (poisoned.Count == 0) return [];

        return poisoned.Select(kv => new DiagnosticFinding
        {
            Code = "RETRIEVAL-POISON",
            Description = $"Memory '{kv.Key[..Math.Min(40, kv.Key.Length)]}...' appeared in {kv.Value}/{rerankLines.Count} retrievals",
            Severity = DiagnosticSeverity.Warning,
            Evidence = $"Appeared in {kv.Value} of {rerankLines.Count} diversity re-rank results",
            SuggestedAction = "High-importance stale memory dominating. Consider reducing importance.",
            AutoCorrectible = true,
        }).ToList();
    }

    /// <summary>4.3 — Inner thought diversity WARNING firing repeatedly.</summary>
    internal static List<DiagnosticFinding> DetectThoughtLoop(IReadOnlyList<string> lines)
    {
        var warningCount = lines.Count(l =>
            l.Contains("Your recent thoughts are repetitive", StringComparison.OrdinalIgnoreCase));
        if (warningCount < 3) return [];

        return [new DiagnosticFinding
        {
            Code = "THOUGHT-LOOP",
            Description = $"Thought diversity WARNING fired {warningCount}x — model stuck on a theme",
            Severity = DiagnosticSeverity.Warning,
            Evidence = $"{warningCount} repetitive thought warnings in scan window",
            SuggestedAction = "Check for high-resonance memory cluster driving the loop.",
        }];
    }

    /// <summary>4.4 — Emotional dimension pegged at extreme for multiple cycles.</summary>
    internal static List<DiagnosticFinding> DetectEmotionalSaturation(IReadOnlyList<string> lines)
    {
        var moodLines = lines.Where(l => l.Contains("Mood: W=") || l.Contains("║  Mood:")).ToList();
        if (moodLines.Count < 3) return [];

        var findings = new List<DiagnosticFinding>();
        // Check if any dimension is consistently extreme
        var extremeCount = new Dictionary<string, int>
        {
            ["Warmth"] = 0, ["Energy"] = 0, ["Worry"] = 0, ["Playfulness"] = 0
        };

        foreach (var line in moodLines)
        {
            var wMatch = Regex.Match(line, @"W=([0-9.]+)");
            var eMatch = Regex.Match(line, @"E=([0-9.]+)");
            var cMatch = Regex.Match(line, @"C=([0-9.]+)");
            var pMatch = Regex.Match(line, @"P=([0-9.]+)");

            if (wMatch.Success && float.TryParse(wMatch.Groups[1].Value, out var w) && (w >= 0.95f || w <= 0.05f))
                extremeCount["Warmth"]++;
            if (eMatch.Success && float.TryParse(eMatch.Groups[1].Value, out var e) && (e >= 0.95f || e <= 0.05f))
                extremeCount["Energy"]++;
            if (cMatch.Success && float.TryParse(cMatch.Groups[1].Value, out var c) && (c >= 0.95f || c <= 0.05f))
                extremeCount["Worry"]++;
            if (pMatch.Success && float.TryParse(pMatch.Groups[1].Value, out var p) && (p >= 0.95f || p <= 0.05f))
                extremeCount["Playfulness"]++;
        }

        foreach (var (dim, count) in extremeCount)
        {
            if (count >= 3)
            {
                findings.Add(new DiagnosticFinding
                {
                    Code = "EMOTIONAL-SATURATION",
                    Description = $"{dim} pegged at extreme in {count}/{moodLines.Count} cycles",
                    Severity = DiagnosticSeverity.Warning,
                    Evidence = $"{dim} at 0.95+ or 0.05- for {count} consecutive readings",
                    SuggestedAction = "Check contribution pruning and tanh scale.",
                });
            }
        }

        return findings;
    }

    /// <summary>4.5 — Mark correcting Ani (confabulation detected by contact).</summary>
    internal static List<DiagnosticFinding> DetectConfabulationCorrection(IReadOnlyList<string> lines)
    {
        string[] correctionPatterns =
        [
            "no,? that's not", "you don't know", "stop guessing", "that's not right",
            "that's not correct", "please don't guess", "you're confused",
            "i didn't say that", "that's wrong", "not what i said",
        ];

        var corrections = lines.Count(l =>
            l.Contains("Inbound SMS from Mark:") &&
            correctionPatterns.Any(p => Regex.IsMatch(l, p, RegexOptions.IgnoreCase)));

        if (corrections == 0) return [];

        var severity = corrections >= 3 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info;
        return [new DiagnosticFinding
        {
            Code = "CONFABULATION-CORRECTION",
            Description = $"Contact corrected confabulation {corrections}x in scan window",
            Severity = severity,
            Evidence = $"{corrections} correction patterns detected in inbound messages",
            SuggestedAction = "Log for Phase 5c harvest exclusion. If repeated, investigate retrieval quality.",
        }];
    }

    /// <summary>4.6 — Same record merged repeatedly (quality gate should catch).</summary>
    internal static List<DiagnosticFinding> DetectMergeStorm(IReadOnlyList<string> lines)
    {
        var mergeLines = lines.Where(l => l.Contains("Memory merge: updated")).ToList();
        if (mergeLines.Count < 3) return [];

        // Extract merged IDs and count repetitions
        var idCounts = new Dictionary<string, int>();
        foreach (var line in mergeLines)
        {
            var match = Regex.Match(line, @"Memory merge: updated ([a-f0-9-]+)");
            if (match.Success)
                idCounts[match.Groups[1].Value] = idCounts.GetValueOrDefault(match.Groups[1].Value) + 1;
        }

        return idCounts.Where(kv => kv.Value >= 3).Select(kv => new DiagnosticFinding
        {
            Code = "MERGE-STORM",
            Description = $"Memory {kv.Key[..8]}... merged {kv.Value}x in scan window",
            Severity = DiagnosticSeverity.Warning,
            Evidence = $"Same record merged {kv.Value} times",
            SuggestedAction = "Quality gate may not be catching iterative rewrites. Check ContainsNovelSpecifics.",
        }).ToList();
    }

    /// <summary>4.7 — Daily outreach limit reached early.</summary>
    internal static List<DiagnosticFinding> DetectOutreachBlocked(IReadOnlyList<string> lines)
    {
        var blocked = lines.Where(l => l.Contains("Daily outreach limit reached")).ToList();
        if (blocked.Count < 5) return [];

        // Check if the first block happened before noon
        var firstBlock = blocked.FirstOrDefault();
        if (firstBlock is null) return [];

        var timeMatch = Regex.Match(firstBlock, @"^(\d{2}):(\d{2}):");
        if (!timeMatch.Success) return [];

        var hour = int.Parse(timeMatch.Groups[1].Value);
        if (hour >= 12) return []; // Afternoon blocking is normal

        return [new DiagnosticFinding
        {
            Code = "OUTREACH-BLOCKED",
            Description = $"Daily outreach limit reached by {hour}:00 — {blocked.Count} blocked attempts since",
            Severity = DiagnosticSeverity.Info,
            Evidence = $"First block at {timeMatch.Value}, {blocked.Count} total blocked",
            SuggestedAction = "Check if conversation replies are consuming outreach budget.",
        }];
    }

    /// <summary>4.8 — Outreach decision reasoning conflicts with actual time.</summary>
    internal static List<DiagnosticFinding> DetectTemporalConfab(IReadOnlyList<string> lines)
    {
        var decisionLines = lines.Where(l => l.Contains("Outreach decision:")).ToList();
        if (decisionLines.Count == 0) return [];

        var findings = new List<DiagnosticFinding>();
        foreach (var line in decisionLines)
        {
            var timeMatch = Regex.Match(line, @"^(\d{2}):(\d{2}):");
            if (!timeMatch.Success) continue;

            var actualHour = int.Parse(timeMatch.Groups[1].Value);
            var isAM = actualHour < 12;

            // Check if reasoning mentions wrong time of day
            var mentionsMidnight = line.Contains("midnight", StringComparison.OrdinalIgnoreCase);
            var mentionsMorning = line.Contains("morning", StringComparison.OrdinalIgnoreCase);

            if (isAM && mentionsMidnight)
            {
                findings.Add(new DiagnosticFinding
                {
                    Code = "TEMPORAL-CONFAB",
                    Description = $"Outreach reasoning mentions 'midnight' at {actualHour}:{timeMatch.Groups[2].Value} AM",
                    Severity = DiagnosticSeverity.Info,
                    Evidence = line[..Math.Min(120, line.Length)],
                    SuggestedAction = "Model ignoring time injection. Monitor frequency.",
                });
            }
            else if (!isAM && actualHour < 20 && mentionsMorning)
            {
                findings.Add(new DiagnosticFinding
                {
                    Code = "TEMPORAL-CONFAB",
                    Description = $"Outreach reasoning mentions 'morning' at {actualHour}:{timeMatch.Groups[2].Value}",
                    Severity = DiagnosticSeverity.Info,
                    Evidence = line[..Math.Min(120, line.Length)],
                    SuggestedAction = "Model ignoring time injection. Monitor frequency.",
                });
            }
        }

        return findings;
    }

    /// <summary>4.9 — Long conversation thread with degrading quality signals.</summary>
    internal static List<DiagnosticFinding> DetectConversationHealth(IReadOnlyList<string> lines)
    {
        // Count echo guard checks (indicates thread length) and compression events
        var echoChecks = lines.Where(l => l.Contains("Echo guard: checking reply against")).ToList();
        var compressions = lines.Count(l => l.Contains("Context compression:"));

        if (echoChecks.Count == 0) return [];

        // Extract max thread message count from echo guard lines
        var maxMessages = 0;
        foreach (var line in echoChecks)
        {
            var match = Regex.Match(line, @"against (\d+) prior messages");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
                maxMessages = Math.Max(maxMessages, count);
        }

        if (maxMessages < 15) return [];

        return [new DiagnosticFinding
        {
            Code = "LONG-THREAD",
            Description = $"Thread at {maxMessages} messages with {compressions} compressions",
            Severity = DiagnosticSeverity.Info,
            Evidence = $"Max thread length: {maxMessages}, compressions: {compressions}",
            SuggestedAction = "Long thread may be degrading quality. Monitor for echo loops.",
        }];
    }

    /// <summary>4.10 — Same topic recurring in inner thoughts across many cycles.</summary>
    internal static List<DiagnosticFinding> DetectPerceptionAnchor(IReadOnlyList<string> lines)
    {
        // Extract inner thought content and look for repeated key phrases
        var thoughtLines = lines
            .Where(l => l.Contains("Inner thought (valence="))
            .ToList();

        if (thoughtLines.Count < 5) return [];

        // Extract short content snippets and find repeated phrases
        var phraseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in thoughtLines)
        {
            // Look for distinctive phrases (4+ words) that appear in the thought
            var content = line[(line.IndexOf("): ", StringComparison.Ordinal) + 3)..];

            // Extract notable phrases — numbers with context, quoted phrases, distinctive fragments
            var numberPhrases = Regex.Matches(content, @"\b\d+\s+degrees?\b|\b\d+°");
            foreach (Match m in numberPhrases)
                phraseCounts[m.Value] = phraseCounts.GetValueOrDefault(m.Value) + 1;

            var quotedPhrases = Regex.Matches(content, @"'[^']{10,40}'");
            foreach (Match m in quotedPhrases)
                phraseCounts[m.Value] = phraseCounts.GetValueOrDefault(m.Value) + 1;
        }

        var anchored = phraseCounts.Where(kv => kv.Value >= 4).ToList();
        if (anchored.Count == 0) return [];

        return anchored.Select(kv => new DiagnosticFinding
        {
            Code = "PERCEPTION-ANCHOR",
            Description = $"Inner thoughts anchored on '{kv.Key}' — appeared in {kv.Value}/{thoughtLines.Count} thoughts",
            Severity = kv.Value >= 6 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
            Evidence = $"Phrase '{kv.Key}' in {kv.Value} of {thoughtLines.Count} inner thoughts",
            SuggestedAction = "A perception or memory is anchoring the inner thought model on a single theme. The theme may be valid (new information being processed) or a stuck loop.",
        }).ToList();
    }

    // ── Log File Reader ───────────────────────────────────────────────────

    private async Task<List<string>> ReadRecentLogLinesAsync(int maxLines, CancellationToken ct)
    {
        // Find today's log file
        var today = DateTime.Now.ToString("yyyyMMdd");
        var logFile = Path.Combine(_logDirectory, $"ani-debug-{today}.log");

        if (!File.Exists(logFile))
        {
            _log.LogDebug("No log file found at {Path}", logFile);
            return [];
        }

        // Read last N lines using a shared-read file stream (Serilog holds the write lock)
        var allLines = new List<string>();
        using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            allLines.Add(line);

        // Return last maxLines
        return allLines.Count <= maxLines
            ? allLines
            : allLines.GetRange(allLines.Count - maxLines, maxLines);
    }

    private static TimeSpan EstimateTimeWindow(IReadOnlyList<string> lines)
    {
        if (lines.Count < 2) return TimeSpan.Zero;

        // Parse timestamps from first and last lines (format: "HH:mm:ss")
        var firstTime = ParseLogTime(lines[0]);
        var lastTime = ParseLogTime(lines[^1]);

        if (firstTime is null || lastTime is null) return TimeSpan.Zero;

        var span = lastTime.Value - firstTime.Value;
        return span < TimeSpan.Zero ? span.Add(TimeSpan.FromDays(1)) : span;
    }

    private static TimeSpan? ParseLogTime(string line)
    {
        var match = Regex.Match(line, @"^(\d{2}):(\d{2}):(\d{2})");
        if (!match.Success) return null;

        return new TimeSpan(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value));
    }
}
