using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Coreference;

/// <summary>
/// Theme N Phase N.2 (May 8, 2026) — deterministic frame ranking.
///
/// Implements <see cref="IOutreachFrameSelector"/> by ranking candidate
/// substrate items across the four source-frames against the snapshot's
/// already-populated lists. No fresh memory queries; no LLM calls. Pure
/// deterministic ranking on the snapshot surface ContextBuilder produced.
///
/// **Algorithm:**
/// <list type="number">
///   <item>Per-tier candidate retrieval from the snapshot (see source-tier mapping in
///   <c>docs/spec/ANI-Tier-Interface-Contract-1-Pager.md</c> §6).</item>
///   <item>Score each candidate: <c>recency × salience × frame-specific weight</c>.</item>
///   <item>Pick the single highest-scoring candidate globally; the frame type follows
///   from which tier the candidate belongs to.</item>
///   <item>If <c>top_score &lt; <see cref="MinAcceptableScore"/></c>, return
///   <see cref="OutreachFrame.None"/> (suppress outreach — substrate too thin).</item>
///   <item>Confidence = <c>top_score / (top_score + 0.2)</c> — bounded 0-1, asymptotes
///   toward 1.0 as the score grows. See <see cref="ComputeConfidence"/>.</item>
/// </list>
///
/// **Tie-break order** (when scores are within float-equality of each other):
/// <c>SHARED &gt; ANI_DOMAIN &gt; ANI_INTERIOR &gt; WORLD_PERCEPTION</c>. Reflects the
/// project's preference for grounded shared content over Ani's interior content
/// when both are equally available — mirror-trap risk is lower when there is
/// genuine shared substrate to anchor against.
///
/// **Telemetry:** emits <c>N4_FRAME_SELECTED</c> on a successful pick and
/// <c>N4_FRAME_SUPPRESSED</c> on the substrate-thin path. See
/// <c>docs/spec/ANI-Theme-M2-Telemetry-Catalog.md</c> §6 (N.4 entry).
/// </summary>
public sealed class OutreachFrameSelector : IOutreachFrameSelector
{
    // ── Frame-specific weights ───────────────────────────────────────────
    // Start equal across all four frames; let telemetry reveal whether
    // any of them need re-balancing in N.4. Deferring AniOptions wiring
    // until empirical evidence justifies the tuning surface.
    private const float WeightShared          = 1.0f;
    private const float WeightAniDomain       = 1.0f;
    private const float WeightAniInterior     = 1.0f;
    private const float WeightWorldPerception = 1.0f;

    // ── Recency lookback windows ─────────────────────────────────────────
    private static readonly TimeSpan SharedLookback         = TimeSpan.FromDays(7);
    // ANI_DOMAIN: canonical seed material — no recency decay.
    private static readonly TimeSpan AniInteriorLookback    = TimeSpan.FromHours(24);
    private static readonly TimeSpan WorldPerceptionLookback = TimeSpan.FromHours(2);

    // ── Substrate-thin floor ─────────────────────────────────────────────
    // Below this top score, we suppress (return None). Conservative on
    // purpose — Mark's directive: better silence than ungrounded reach.
    private const float MinAcceptableScore = 0.15f;

    // ── Default salience for snapshot items without an Importance value ──
    private const float DefaultSalience = 0.5f;

    // ── Anchor preview length ────────────────────────────────────────────
    private const int AnchorMaxChars = 120;
    private const int AnchorPreviewLogChars = 60;

    // ── Mark-asserted Facts content prefixes (SHARED candidate filter) ───
    private static readonly string[] MarkAssertedPrefixes =
    {
        "Mark said:",
        "Mark texted:",
    };

    // ── Source-name buckets ──────────────────────────────────────────────
    private const string CharacterSeedSource = "character-seed";
    private static readonly HashSet<string> WorldPerceptionSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "rss",
        "weather",
        "twilio-weather",
        "world-weather",
    };

    private readonly ILogger<OutreachFrameSelector> _log;

    public OutreachFrameSelector(ILogger<OutreachFrameSelector> log)
    {
        _log = log;
    }

    public Task<IOutreachFrameEnvelope> SelectFrameAsync(
        ContextSnapshot snapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        var now = snapshot.BuiltAt;
        var candidates = new List<ScoredCandidate>();

        candidates.AddRange(CollectSharedCandidates(snapshot, now));
        candidates.AddRange(CollectAniDomainCandidates(snapshot));
        candidates.AddRange(CollectAniInteriorCandidates(snapshot, now));
        candidates.AddRange(CollectWorldPerceptionCandidates(snapshot, now));

        if (candidates.Count == 0)
        {
            return Task.FromResult<IOutreachFrameEnvelope>(LogAndReturnSuppressed(0f));
        }

        // Pick top candidate globally. Tie-break by frame priority.
        var top = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => FramePriority(c.FrameType))
            .First();

        if (top.Score < MinAcceptableScore)
        {
            return Task.FromResult<IOutreachFrameEnvelope>(LogAndReturnSuppressed(top.Score));
        }

        var anchor    = TruncateAnchor(top.Anchor);
        var confidence = ComputeConfidence(top.Score);
        var frame     = new OutreachFrame(top.FrameType, anchor, confidence);
        // F-1 Phase 8b (2026-08-19) — wrap the frame record in the
        // producer-boundary envelope. Consumers use envelope passthrough
        // properties (FrameType/Anchor/Confidence) or unwrap via .Frame
        // when they need the record itself.
        var envelope = new OutreachFrameEnvelope { Frame = frame };

        _log.LogInformation(
            "N4_FRAME_SELECTED frame={Frame} confidence={Confidence:0.000} score={Score:0.000} anchor_preview=\"{AnchorPreview}\"",
            top.FrameType,
            confidence,
            top.Score,
            PreviewForLog(anchor));

        return Task.FromResult<IOutreachFrameEnvelope>(envelope);
    }

    private OutreachFrameEnvelope LogAndReturnSuppressed(float topScore)
    {
        _log.LogInformation(
            "N4_FRAME_SUPPRESSED frame=None reason=\"substrate-thin top_score={TopScore:0.000}\"",
            topScore);
        return OutreachFrameEnvelope.None;
    }

    // ─── SHARED candidates ────────────────────────────────────────────────

    private static IEnumerable<ScoredCandidate> CollectSharedCandidates(
        ContextSnapshot snapshot, DateTimeOffset now)
    {
        // Closed-conversation gist (V1.4 — paraphrased, anti-parrot at substrate).
        var closed = snapshot.RecentClosedConversation;
        if (closed is not null && !string.IsNullOrWhiteSpace(closed.Gist))
        {
            var recency  = LinearRecency(closed.ClosedAt, now, SharedLookback);
            var salience = DefaultSalience;
            var score    = recency * salience * WeightShared;
            yield return new ScoredCandidate(
                OutreachFrameType.Shared, score, closed.Gist);
        }

        // Mark-asserted Facts from RelevantMemory.
        foreach (var m in snapshot.RelevantMemory)
        {
            if (m.Provenance != EpistemicTier.Facts) continue;
            if (!IsMarkAsserted(m.Content)) continue;

            var recency  = LinearRecency(m.OccurredAt, now, SharedLookback);
            if (recency <= 0f) continue;
            var salience = SalienceFor(m);
            var score    = recency * salience * WeightShared;
            yield return new ScoredCandidate(
                OutreachFrameType.Shared, score, m.Content);
        }
    }

    private static bool IsMarkAsserted(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        for (var i = 0; i < MarkAssertedPrefixes.Length; i++)
        {
            if (content.StartsWith(MarkAssertedPrefixes[i], StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ─── ANI_DOMAIN candidates ────────────────────────────────────────────

    private static IEnumerable<ScoredCandidate> CollectAniDomainCandidates(
        ContextSnapshot snapshot)
    {
        // No recency decay — canonical character-seed material is timeless.
        foreach (var m in snapshot.RelevantMemory)
        {
            if (m.Provenance != EpistemicTier.Facts) continue;
            if (!string.Equals(m.SourceName, CharacterSeedSource, StringComparison.OrdinalIgnoreCase))
                continue;

            var salience = SalienceFor(m);
            var score    = 1.0f * salience * WeightAniDomain;
            yield return new ScoredCandidate(
                OutreachFrameType.AniDomain, score, m.Content);
        }
    }

    // ─── ANI_INTERIOR candidates ──────────────────────────────────────────

    private static IEnumerable<ScoredCandidate> CollectAniInteriorCandidates(
        ContextSnapshot snapshot, DateTimeOffset now)
    {
        // Recent inner thoughts — already similarity-ranked by ContextBuilder,
        // but re-score here against a uniform 24h lookback so they compete
        // fairly with other tiers.
        foreach (var m in snapshot.SimilarRecentThoughts)
        {
            var recency = LinearRecency(m.OccurredAt, now, AniInteriorLookback);
            if (recency <= 0f) continue;
            var salience = SalienceFor(m);
            var score    = recency * salience * WeightAniInterior;
            yield return new ScoredCandidate(
                OutreachFrameType.AniInterior, score, m.Content);
        }

        foreach (var m in snapshot.RelevantMemory)
        {
            if (m.Provenance != EpistemicTier.Interior) continue;
            var recency = LinearRecency(m.OccurredAt, now, AniInteriorLookback);
            if (recency <= 0f) continue;
            var salience = SalienceFor(m);
            var score    = recency * salience * WeightAniInterior;
            yield return new ScoredCandidate(
                OutreachFrameType.AniInterior, score, m.Content);
        }
    }

    // ─── WORLD_PERCEPTION candidates ──────────────────────────────────────

    private static IEnumerable<ScoredCandidate> CollectWorldPerceptionCandidates(
        ContextSnapshot snapshot, DateTimeOffset now)
    {
        foreach (var p in snapshot.Perceptions)
        {
            var recency = LinearRecency(p.OccurredAt, now, WorldPerceptionLookback);
            if (recency <= 0f) continue;
            // PerceptionEvent has no Importance; use default salience scaled by
            // ContactRelevance (range 0-1 already).
            var salience = p.ContactRelevance > 0f ? p.ContactRelevance : DefaultSalience;
            var score    = recency * salience * WeightWorldPerception;
            var anchor   = !string.IsNullOrWhiteSpace(p.Summary) ? p.Summary : p.SourceName;
            yield return new ScoredCandidate(
                OutreachFrameType.WorldPerception, score, anchor);
        }

        foreach (var m in snapshot.RelevantMemory)
        {
            if (m.Provenance != EpistemicTier.Facts) continue;
            if (m.SourceName is null) continue;
            if (!WorldPerceptionSources.Contains(m.SourceName)) continue;

            var recency = LinearRecency(m.OccurredAt, now, WorldPerceptionLookback);
            if (recency <= 0f) continue;
            var salience = SalienceFor(m);
            var score    = recency * salience * WeightWorldPerception;
            yield return new ScoredCandidate(
                OutreachFrameType.WorldPerception, score, m.Content);
        }
    }

    // ─── Scoring helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Linear decay from 1.0 at <paramref name="now"/> to 0.0 at the lookback
    /// edge. Items older than the lookback return 0.0 (they're effectively
    /// out-of-scope for the frame).
    /// </summary>
    private static float LinearRecency(DateTimeOffset occurredAt, DateTimeOffset now, TimeSpan lookback)
    {
        var elapsed = now - occurredAt;
        if (elapsed <= TimeSpan.Zero) return 1.0f;
        if (elapsed >= lookback)      return 0.0f;
        return 1.0f - (float)(elapsed.TotalMilliseconds / lookback.TotalMilliseconds);
    }

    private static float SalienceFor(MemoryRecord m)
    {
        // MemoryRecord.Importance is in [0,1] when populated. Fall back to
        // a neutral default for snapshot items without an explicit score.
        return m.Importance > 0f ? Math.Clamp(m.Importance, 0f, 1f) : DefaultSalience;
    }

    /// <summary>
    /// Maps a top score into a 0-1 confidence asymptote. Starting heuristic
    /// per N.2 plan; expose as a private static so it can be swapped without
    /// touching the ranking algorithm.
    /// </summary>
    private static float ComputeConfidence(float topScore)
    {
        if (topScore <= 0f) return 0f;
        return topScore / (topScore + 0.2f);
    }

    /// <summary>
    /// Lower number = higher priority. SHARED &gt; ANI_DOMAIN &gt; ANI_INTERIOR &gt;
    /// WORLD_PERCEPTION. Used as the secondary key when two candidates have
    /// equal scores so the result remains deterministic.
    /// </summary>
    private static int FramePriority(OutreachFrameType type) => type switch
    {
        OutreachFrameType.Shared          => 0,
        OutreachFrameType.AniDomain       => 1,
        OutreachFrameType.AniInterior     => 2,
        OutreachFrameType.WorldPerception => 3,
        _                                 => 99,
    };

    private static string TruncateAnchor(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        return content.Length <= AnchorMaxChars
            ? content
            : content.Substring(0, AnchorMaxChars);
    }

    private static string PreviewForLog(string anchor)
    {
        if (string.IsNullOrEmpty(anchor)) return string.Empty;
        return anchor.Length <= AnchorPreviewLogChars
            ? anchor
            : anchor.Substring(0, AnchorPreviewLogChars);
    }

    /// <summary>Internal scored candidate before the final frame is picked.</summary>
    private readonly record struct ScoredCandidate(
        OutreachFrameType FrameType,
        float             Score,
        string            Anchor);
}
