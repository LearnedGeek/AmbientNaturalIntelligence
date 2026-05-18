using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Feature 41: Scheduled diagnostic runner with escalating auto-correction.
/// Runs the diagnostic service every N minutes and logs findings.
///
/// Auto-correction escalation:
///   Scan 1: Log finding, reduce importance by 0.3
///   Scan 2: Log finding again, reduce importance further
///   Scan 3+: Same topic still poisoning → delete InnerThought memories
///            driving the loop (conversation/episodic memories preserved)
/// </summary>
public class DiagnosticScheduler : BackgroundService
{
    private readonly IDiagnosticService _diagnostic;
    private readonly IMemoryPersistence _persist;
    private readonly IMemorySearch _search;
    private readonly DiagnosticOptions _options;
    private readonly ILogger<DiagnosticScheduler> _log;

    // Track how many consecutive scans each finding code+topic has appeared
    private readonly Dictionary<string, int> _recurringFindings = new();

    public DiagnosticScheduler(
        IDiagnosticService diagnostic,
        IMemoryPersistence persist,
        IMemorySearch search,
        IOptions<DiagnosticOptions> options,
        ILogger<DiagnosticScheduler> log)
    {
        _diagnostic = diagnostic;
        _persist = persist;
        _search = search;
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

                // Auto-correction with escalation
                if (_options.AutoCorrectEnabled)
                    await RunAutoCorrectionsAsync(report, ct).ConfigureAwait(false);

                // Track recurring findings and clear resolved ones
                UpdateRecurringFindings(report);
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

    private async Task RunAutoCorrectionsAsync(DiagnosticReport report, CancellationToken ct)
    {
        foreach (var finding in report.Findings)
        {
            if (!finding.AutoCorrectible) continue;
            if (!_options.AutoCorrectAllowlist.Contains(finding.Code)) continue;

            var findingKey = $"{finding.Code}:{finding.Description[..Math.Min(40, finding.Description.Length)]}";
            var recurrence = _recurringFindings.GetValueOrDefault(findingKey);

            if (finding.Code == "RETRIEVAL-POISON")
                await HandleRetrievalPoisonAsync(finding, recurrence, ct).ConfigureAwait(false);
        }

        // Also handle PERCEPTION-ANCHOR escalation (not marked AutoCorrectible,
        // but we escalate to memory cleanup after 3+ consecutive scans)
        foreach (var finding in report.Findings.Where(f => f.Code == "PERCEPTION-ANCHOR"))
        {
            var findingKey = $"{finding.Code}:{finding.Description[..Math.Min(40, finding.Description.Length)]}";
            var recurrence = _recurringFindings.GetValueOrDefault(findingKey);

            if (recurrence >= 3)
                await HandlePerceptionAnchorEscalationAsync(finding, recurrence, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Escalating auto-correction for retrieval poisoning.
    /// Scan 1-2: reduce importance by 0.3.
    /// Scan 3+: delete InnerThought memories matching the poisoned topic.
    /// </summary>
    private async Task HandleRetrievalPoisonAsync(
        DiagnosticFinding finding, int recurrence, CancellationToken ct)
    {
        // AUTO-DELETION DISABLED — diagnostic-only mode.
        // The auto-corrector deleted 128 valid inner thoughts in one afternoon
        // (April 5, 2026) because Spanish tutoring memories were legitimately relevant.
        // Retrieval-poison detection remains active for monitoring but no longer
        // modifies or deletes memories. The immune system is an observer, not a surgeon.
        //
        // Root cause: retrieval can't distinguish "relevant" from "dominating."
        // Fix: World Layer + diversity improvements, not deletion.
        if (true) // was: recurrence < 6
        {
            // Actually reduce importance of the poisoning memory by 0.15 per scan.
            // Extract the memory content prefix from the finding description.
            // Format: "Memory 'About Mark: Learning Spanish o...' appeared in X/Y retrievals"
            var memoryPrefix = ExtractMemoryPrefixFromFinding(finding.Description);
            if (memoryPrefix is not null)
            {
                var reduced = await ReduceMemoryImportanceAsync(memoryPrefix, 0.15f, ct).ConfigureAwait(false);
                _log.LogInformation("Auto-correct [RETRIEVAL-POISON] scan {Recurrence}: reduced importance by 0.15 on '{Topic}' (found={Found})",
                    recurrence + 1, memoryPrefix, reduced);
            }
            else
            {
                _log.LogInformation("Auto-correct [RETRIEVAL-POISON] scan {Recurrence}: could not extract memory prefix from '{Topic}'",
                    recurrence + 1, finding.Description[..Math.Min(60, finding.Description.Length)]);
            }
            return;
        }

        // DELETION DISABLED — log only. See comment above.
        _log.LogWarning("Auto-correct [RETRIEVAL-POISON] scan {Recurrence}: topic still recurring (deletion disabled, diagnostic only)",
            recurrence + 1);
    }

    /// <summary>
    /// Extract the memory content prefix from a retrieval-poison finding description.
    /// Input: "Memory 'About Mark: Learning Spanish o...' appeared in 7/12 retrievals"
    /// Output: "About Mark: Learning Spanish o"
    /// </summary>
    private static string? ExtractMemoryPrefixFromFinding(string description)
    {
        var start = description.IndexOf('\'');
        var end = description.IndexOf("...'", StringComparison.Ordinal);
        if (start < 0 || end < 0 || end <= start) return null;
        return description[(start + 1)..end];
    }

    /// <summary>
    /// Reduce importance of memories matching a content prefix by the specified amount.
    /// Minimum importance is 0.1 — never fully zero out a memory.
    ///
    /// Apr 11 fix: previously this used semantic search (embedding similarity) to find
    /// the memory, then a StartsWith filter to confirm. The embedding path doesn't
    /// reliably surface prefix-matched memories — it surfaces SEMANTICALLY similar ones.
    /// The Apr 11 ani-debug log showed four consecutive scans of the same "not teaching"
    /// memory, all reporting found=false, because the embedding search wasn't returning
    /// the exact record whose content starts with the logged prefix. Fix: scan broader
    /// candidate sets (by type and by content prefix) until we find the actual record.
    /// </summary>
    private async Task<bool> ReduceMemoryImportanceAsync(string contentPrefix, float reduction, CancellationToken ct)
    {
        try
        {
            // First, try semantic search (works for topically-similar memories).
            var memories = await _search.SearchWithScoresAsync(contentPrefix, 10, ct).ConfigureAwait(false);
            var match = memories
                .Select(m => m.Record)
                .FirstOrDefault(r => r.Content.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase));

            // Fallback: scan the most common memory types and do a direct prefix check.
            // This catches cases where semantic search didn't rank the target in the top-10
            // (e.g., a Mark-quote memory whose embedding isn't closest to the prefix text).
            if (match is null)
            {
                var candidateTypes = new[]
                {
                    Core.Models.MemoryType.Episodic,
                    Core.Models.MemoryType.Perception,
                    Core.Models.MemoryType.Semantic,
                    Core.Models.MemoryType.InnerThought,
                };
                foreach (var type in candidateTypes)
                {
                    var typeRecords = await _search.GetByTypeAsync(type, 500, ct).ConfigureAwait(false);
                    match = typeRecords.FirstOrDefault(r =>
                        r.Content.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) break;
                }
            }

            if (match is null) return false;

            match.Importance = Math.Max(0.1f, match.Importance - reduction);
            await _persist.SaveAsync(match, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to reduce importance for '{Prefix}'", contentPrefix);
            return false;
        }
    }

    /// <summary>
    /// Escalating auto-correction for perception anchoring.
    /// After 3+ consecutive scans with the same anchor, delete InnerThought memories
    /// that contain the anchored phrase. Conversation/episodic memories are preserved.
    /// </summary>
    private Task HandlePerceptionAnchorEscalationAsync(
        DiagnosticFinding finding, int recurrence, CancellationToken ct)
    {
        // DELETION DISABLED — diagnostic-only mode. Same reasoning as retrieval-poison.
        _log.LogWarning("Auto-correct [PERCEPTION-ANCHOR] scan {Recurrence}: topic still anchored (deletion disabled, diagnostic only)",
            recurrence + 1);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Delete InnerThought memories matching a topic from a retrieval-poison finding.
    /// Extracts the topic snippet and searches for matching InnerThought records.
    /// Only deletes InnerThought type — preserves Episodic, Semantic, Perception.
    /// </summary>
    private async Task DeleteInnerThoughtsByTopicAsync(string findingDescription, CancellationToken ct)
    {
        // Extract the memory snippet from "Memory 'SNIPPET...' appeared in X/Y"
        var match = SafeRegex.Match(
            findingDescription, @"Memory '([^']+)'");
        if (!match.Success) return;

        var snippet = match.Groups[1].Value.TrimEnd('.');

        // Search for InnerThought memories containing this snippet
        var thoughts = await _search.GetByTypeAsync(MemoryType.InnerThought, 200, ct)
            .ConfigureAwait(false);
        var toDelete = thoughts
            .Where(m => m.Content.Contains(snippet, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (toDelete.Count == 0) return;

        foreach (var memory in toDelete)
            await _persist.DeleteAsync(memory.Id, ct).ConfigureAwait(false);

        _log.LogWarning("Auto-correct: deleted {Count} InnerThought memories matching '{Snippet}'",
            toDelete.Count, snippet[..Math.Min(40, snippet.Length)]);
    }

    /// <summary>
    /// Delete InnerThought memories containing a specific anchored phrase.
    /// Used for PERCEPTION-ANCHOR escalation.
    /// </summary>
    private async Task DeleteInnerThoughtsByPhraseAsync(string phrase, CancellationToken ct)
    {
        var thoughts = await _search.GetByTypeAsync(MemoryType.InnerThought, 200, ct)
            .ConfigureAwait(false);
        var toDelete = thoughts
            .Where(m => m.Content.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (toDelete.Count == 0) return;

        foreach (var memory in toDelete)
            await _persist.DeleteAsync(memory.Id, ct).ConfigureAwait(false);

        _log.LogWarning("Auto-correct: deleted {Count} InnerThought memories containing '{Phrase}'",
            toDelete.Count, phrase);
    }

    /// <summary>
    /// Track which findings recur across consecutive scans.
    /// Findings that disappear reset their counter.
    /// </summary>
    private void UpdateRecurringFindings(DiagnosticReport report)
    {
        var currentKeys = new HashSet<string>();

        foreach (var finding in report.Findings)
        {
            var key = $"{finding.Code}:{finding.Description[..Math.Min(40, finding.Description.Length)]}";
            currentKeys.Add(key);
            _recurringFindings[key] = _recurringFindings.GetValueOrDefault(key) + 1;
        }

        // Clear findings that resolved
        var resolved = _recurringFindings.Keys.Where(k => !currentKeys.Contains(k)).ToList();
        foreach (var key in resolved)
            _recurringFindings.Remove(key);
    }
}
