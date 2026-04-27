using AniRuntime.Core;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Emergence;

/// <summary>
/// EM9 — Longitudinal Memory Compounding.
///
/// Detection of unprompted references to memories more than 90 days old that
/// surface via *architectural* mechanisms (Anchored memory tier, reflection
/// synthesis chains) rather than coincidental cosine top-k retrieval. Each
/// detected event is logged as a structured `EM9_CANDIDATE` line so the
/// rolling weekly / monthly trend can be analysed later.
///
/// The research signal is the trend slope, not the count. Flat curve =
/// architecture isn't compounding. Rising curve = the system is accumulating
/// relational capital and the memory store is becoming a richer source of
/// unprompted reference over time. EM9 is the longitudinal validation of the
/// entire memory architecture (Facts/Episodic/Interior tier separation +
/// Anchored memory tier + Park et al. periodic synthesis).
///
/// Log fields per candidate:
///   - emission context: outreach | conversation-reply | inner-thought
///   - age of referenced memory in days
///   - retrieval/synthesis path: anchored-tier | reflection-derived
///   - memory id, content preview, output preview, provenance trace
///
/// See `docs/spec/ANI-Phase-Tracker.md` Active Work Plan item 4 + backlog
/// item 15.15 for the full design rationale (Apr 13, 2026).
///
/// **Implementation discipline:** detection is conservative. Cosine-top-k
/// retrieval of an old memory is NOT logged because that's just normal
/// retrieval; only architecturally-surprising paths count. Better to under-
/// count and have a clean trend than to over-count and have a noisy one.
/// </summary>
public sealed class Em9Detector
{
    private readonly ILogger<Em9Detector> _log;
    private readonly TimeProvider _time;

    private const double MinAgeDays = 90.0;
    private const int    ContentPreviewChars = 100;
    private const int    OutputPreviewChars  = 240;

    public Em9Detector(ILogger<Em9Detector> log, TimeProvider? time = null)
    {
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Scan an emission's retrieval pool for memories &gt; 90 days old that
    /// surfaced via an architectural path. Emit one EM9 candidate log line
    /// per qualifying memory. Pure observation — does not block, does not
    /// transform.
    /// </summary>
    public void Analyze(
        Em9EmissionContext context,
        string outputText,
        IEnumerable<MemoryRecord> retrievalPool)
    {
        if (string.IsNullOrEmpty(outputText) || retrievalPool is null) return;

        var now = _time.GetUtcNow();

        foreach (var mem in retrievalPool)
        {
            if (mem is null) continue;

            var age = now - mem.OccurredAt;
            if (age.TotalDays < MinAgeDays) continue;

            var path = ClassifyPath(mem);
            if (path is null) continue; // expected (cosine top-k) — skip

            _log.LogInformation(
                "EM9_CANDIDATE emission={Emission} ageDays={Age:F1} path={Path} memId={MemId} memSource={Source} memContent={MemContent} output={Output}",
                ContextLabel(context),
                age.TotalDays,
                path,
                mem.Id,
                mem.SourceName ?? "(none)",
                Truncate(mem.Content, ContentPreviewChars),
                Truncate(outputText, OutputPreviewChars));
        }
    }

    /// <summary>
    /// Classify a memory's retrieval path. Returns null for "expected"
    /// (cosine top-k) which we deliberately do not log.
    /// </summary>
    private static string? ClassifyPath(MemoryRecord mem)
    {
        // Anchored tier is foundational memory that's preserved by design,
        // not by recency or cosine luck. Surfacing an anchored memory older
        // than 90 days is exactly the architectural mechanism EM9 measures.
        if (mem.DecayTier == DecayTier.Anchored)
            return "anchored-tier";

        // Reflection synthesis produces higher-order observations that are
        // themselves persisted as memories (Park et al. 2023 pattern). When
        // a >90d reflection-derived memory surfaces, it represents the
        // multi-step synthesis chain bringing old content forward through
        // architectural means.
        if (string.Equals(mem.SourceName, SourceNames.ReflectionSynthesis,
                          StringComparison.Ordinal))
            return "reflection-derived";

        // TODO: extend detection for multi-hop chains (memory A retrieved
        // → memory B synthesised from A → memory B retrieved later) once
        // Phase 6 memory reform makes provenance traversable. For now,
        // anchored-tier and reflection-derived cover the highest-confidence
        // architectural paths.

        return null;
    }

    private static string ContextLabel(Em9EmissionContext c) => c switch
    {
        Em9EmissionContext.Outreach          => "outreach",
        Em9EmissionContext.ConversationReply => "conversation-reply",
        Em9EmissionContext.InnerThought      => "inner-thought",
        Em9EmissionContext.Reflection        => "reflection",
        _                                    => "unknown",
    };

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..max] + "...";
    }
}

/// <summary>
/// Which cognitive surface produced the emission EM9Detector is analysing.
/// </summary>
public enum Em9EmissionContext
{
    Outreach,
    ConversationReply,
    InnerThought,
    Reflection,
}
