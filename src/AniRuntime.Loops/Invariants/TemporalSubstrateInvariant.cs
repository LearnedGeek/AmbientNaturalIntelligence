using System.Text.RegularExpressions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Door B Truth-Verification, Sub-claim 1 (May 2, 2026) — temporal-anchor
/// substrate verification. Catches claims of the form *"what you said
/// yesterday about X"*, *"you mentioned X earlier"*, *"thinking about
/// what you said this morning"* — checking whether Mark actually said
/// something semantically similar to the claimed content within the
/// asserted time window.
///
/// **The canonical case:** May 2 10:40 outreach *"i've been thinking
/// about what you said yesterday—about Sundays being warmer sometimes?"*
/// — Mark didn't say that. The originating fabrication was an
/// inner-thought write at 10:35 CDT that manufactured the attribution.
/// Sub-claim 1 verifies the temporal anchor by searching closed-
/// conversation gists within the named window for semantically-similar
/// Mark content; if none, the claim is unsupported and dispatch fails.
///
/// **Substrate query:** Uses <see cref="IClosedConversationStore"/>'s
/// most-recent-N gists. Each closed-conversation record has a
/// V1.2-paraphrased gist plus an embedding; semantic similarity against
/// the extracted claim is the matching primitive. V1 does NOT search
/// active-thread <c>conversation_messages</c> directly — closed-
/// conversation gists are sufficient for the canonical case (claims
/// about "yesterday" or earlier reference threads that have closed by
/// the time outreach composes today).
///
/// **Soft-fail-on-substrate-error:** if substrate query throws or the
/// embedding call fails, the invariant returns Pass — gate failures
/// must not block dispatch (same pattern as Door C
/// <c>InnerThoughtBleedInvariant</c>).
///
/// **Type-conditional applicability:** Dispatch sinks for
/// ConversationReply / Outreach / Voice. Same set as the other two
/// Door B sub-claims.
/// </summary>
public sealed class TemporalSubstrateInvariant : ICognitiveOutputInvariant
{
    private readonly IClosedConversationStore                 _store;
    private readonly IOllamaClient                            _ollama;
    private readonly ILogger<TemporalSubstrateInvariant>      _log;

    public string Name => "temporal-substrate";

    /// <summary>
    /// Cosine similarity threshold above which a closed-conversation
    /// gist is considered a semantic match for the asserted claim.
    /// V1 default 0.5 — moderately permissive, avoids over-firing on
    /// mismatched-but-related content. Tune empirically against the
    /// four confirmed regression cases.
    /// </summary>
    public const float SimilarityThreshold = 0.5f;

    /// <summary>
    /// Number of closed-conversation records to pull from the store for
    /// the time-window filter. Bounded so the invariant stays cheap.
    /// </summary>
    public const int MaxRecordsToInspect = 30;

    public TemporalSubstrateInvariant(
        IClosedConversationStore                 store,
        IOllamaClient                            ollama,
        ILogger<TemporalSubstrateInvariant>      log)
    {
        _store  = store;
        _ollama = ollama;
        _log    = log;
    }

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        if (artifact.IntendedSink != CognitiveOutputSink.Dispatch)
            return false;

        return artifact.ProducerKind is
            CognitiveProducerKind.ConversationReply
         or CognitiveProducerKind.Outreach
         or CognitiveProducerKind.Voice;
    }

    public async Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.Content))
            return InvariantResult.Pass();

        var triggerMatch = DetectTrigger(artifact.Content);
        if (triggerMatch is null) return InvariantResult.Pass();

        try
        {
            var (anchorWord, claimContent) = triggerMatch.Value;
            var (windowStart, windowEnd) = ResolveTimeWindow(anchorWord, artifact.GeneratedAt);

            var recent = await _store.GetRecentAsync(MaxRecordsToInspect, ct).ConfigureAwait(false);
            var inWindow = recent
                .Where(r => r.ClosedAt >= windowStart && r.ClosedAt <= windowEnd)
                .ToList();

            if (inWindow.Count == 0)
            {
                _log.LogInformation(
                    "TemporalSubstrate: claim asserts \"you {AnchorWord} ...\" but no closed conversations within window {Start:o}—{End:o}",
                    anchorWord, windowStart, windowEnd);
                return InvariantResult.Fail(
                    $"\"you said ... {anchorWord}\" but no Mark conversation within that window");
            }

            var claimEmbedding = await _ollama.EmbedAsync(claimContent, ct).ConfigureAwait(false);
            if (claimEmbedding.Length == 0)
                return InvariantResult.Pass();   // soft-pass on embedding failure

            var bestMatch = 0f;
            ClosedConversationRecord? bestRecord = null;
            foreach (var record in inWindow)
            {
                if (record.Embedding is null || record.Embedding.Length != claimEmbedding.Length)
                    continue;

                var sim = VectorMath.CosineSimilarity(claimEmbedding, record.Embedding);
                if (sim > bestMatch)
                {
                    bestMatch  = sim;
                    bestRecord = record;
                }
            }

            if (bestMatch >= SimilarityThreshold)
            {
                _log.LogDebug(
                    "TemporalSubstrate: claim verified (cosine={Sim:F3}, record={Rec})",
                    bestMatch, bestRecord?.Id);
                return InvariantResult.Pass();
            }

            _log.LogInformation(
                "TemporalSubstrate: \"you said ... {AnchorWord}\" claim unsupported — best cosine {Sim:F3} below {Threshold:F2} across {Count} in-window record(s)",
                anchorWord, bestMatch, SimilarityThreshold, inWindow.Count);
            return InvariantResult.Fail(
                $"\"you said ... {anchorWord}\" but no semantically-matching Mark conversation within the window (best cosine {bestMatch:F2})");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "TemporalSubstrate evaluation threw — defaulting to Pass (gate failure must NOT block dispatch).");
            return InvariantResult.Pass();
        }
    }

    // ── Helpers (testable via InternalsVisibleTo) ────────────────────────

    /// <summary>
    /// Trigger pattern A — *"you said/mentioned/told me ... &lt;anchor&gt;"*.
    /// The verb-then-anchor form. Matches the May 2 *"what you said
    /// yesterday—about Sundays being warmer sometimes"*.
    /// </summary>
    private static readonly Regex TriggerVerbThenAnchorRegex = new(
        @"\byou\s+(?:said|mentioned|told\s+me|asked|brought\s+up)\b[^.?!]*?\b(yesterday|last\s+week|earlier|this\s+morning|this\s+afternoon|the\s+other\s+day)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Trigger pattern B — *"&lt;anchor&gt; ... you said/mentioned"*.
    /// The anchor-then-verb form. Catches *"yesterday you mentioned X"*.
    /// </summary>
    private static readonly Regex TriggerAnchorThenVerbRegex = new(
        @"\b(yesterday|last\s+week|earlier|this\s+morning|this\s+afternoon|the\s+other\s+day)\b[^.?!]*?\byou\s+(?:said|mentioned|told\s+me)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the claim content fragment after an "about " keyword
    /// or after a dash/em-dash. Returns the original sentence if neither
    /// found — embedding the full sentence is acceptable fallback.
    /// </summary>
    private static readonly Regex ContentAfterAboutRegex = new(
        @"\babout\s+([^.?!]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns (anchor word, claim content) if a trigger pattern fires,
    /// or null if the content does not contain a temporal-substrate claim.
    /// Internal for direct test coverage.
    /// </summary>
    internal static (string AnchorWord, string ClaimContent)? DetectTrigger(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var match = TriggerVerbThenAnchorRegex.Match(content);
        if (!match.Success) match = TriggerAnchorThenVerbRegex.Match(content);
        if (!match.Success) return null;

        var anchor = match.Groups[1].Value.ToLowerInvariant().Trim();

        // Extract claim content. Preference order:
        //   1. "about X" after the trigger → X is the claim
        //   2. The sentence containing the trigger → embed that
        var aboutMatch = ContentAfterAboutRegex.Match(content, match.Index);
        if (aboutMatch.Success)
            return (anchor, aboutMatch.Groups[1].Value.Trim());

        // Fallback — extract the sentence containing the trigger.
        var sentence = ExtractSentence(content, match.Index);
        return (anchor, sentence);
    }

    private static string ExtractSentence(string content, int triggerIndex)
    {
        // Walk backwards/forwards from trigger to sentence boundary.
        var start = content.LastIndexOfAny(new[] { '.', '!', '?', '\n' }, triggerIndex);
        var end   = content.IndexOfAny(new[] { '.', '!', '?', '\n' }, triggerIndex);
        if (start < 0) start = 0; else start += 1;
        if (end   < 0) end   = content.Length;
        return content[start..end].Trim();
    }

    /// <summary>
    /// Resolve the time window described by an anchor word. Conservative
    /// interpretations:
    /// <list type="bullet">
    /// <item>"yesterday" → prior calendar day (00:00–23:59 local)</item>
    /// <item>"last week" → prior 7 days (now−7d → now)</item>
    /// <item>"earlier"/"earlier today" → today before now</item>
    /// <item>"this morning" → today 5:00–12:00</item>
    /// <item>"this afternoon" → today 12:00–17:00</item>
    /// <item>"the other day" → ambiguous; prior 7 days</item>
    /// </list>
    /// </summary>
    internal static (DateTimeOffset Start, DateTimeOffset End) ResolveTimeWindow(
        string anchorWord, DateTimeOffset asOf)
    {
        var localDate = asOf.Date;
        var offset = asOf.Offset;

        switch (anchorWord)
        {
            case "yesterday":
                {
                    var startOfYesterday = new DateTimeOffset(localDate.AddDays(-1), offset);
                    var endOfYesterday   = new DateTimeOffset(localDate.AddDays(-1).AddHours(23).AddMinutes(59).AddSeconds(59), offset);
                    return (startOfYesterday, endOfYesterday);
                }
            case "last week":
            case "the other day":
                {
                    var weekAgo = asOf.AddDays(-7);
                    return (weekAgo, asOf);
                }
            case "earlier":
                {
                    var startOfToday = new DateTimeOffset(localDate, offset);
                    return (startOfToday, asOf);
                }
            case "this morning":
                {
                    var morningStart = new DateTimeOffset(localDate.AddHours(5), offset);
                    var morningEnd   = new DateTimeOffset(localDate.AddHours(12), offset);
                    return (morningStart, morningEnd);
                }
            case "this afternoon":
                {
                    var afternoonStart = new DateTimeOffset(localDate.AddHours(12), offset);
                    var afternoonEnd   = new DateTimeOffset(localDate.AddHours(17), offset);
                    return (afternoonStart, afternoonEnd);
                }
            default:
                {
                    // Defensive fallback — treat unknown anchors as a 24h window.
                    return (asOf.AddDays(-1), asOf);
                }
        }
    }
}
