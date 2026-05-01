using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Theme J Phase J.4 (Apr 30, 2026) — universal anti-parrot invariant.
/// Fails when the artifact's content contains a 7+ token verbatim
/// substring of any
/// <see cref="CognitiveArtifact.ContactRecentMessages"/>.
///
/// **Why 7 tokens**: matches the Vibe Loop V1.2 gist-prompt threshold
/// (*"do not lift phrases of 7 or more consecutive words"*). When V1.2's
/// inline anti-parrot prompt is hoisted to share this primitive (per
/// Apr 30 consolidation decision), both surfaces will use the same
/// threshold by reference.
///
/// **Type-conditional applicability**: only applies when the output is
/// a contact-facing surface (Dispatch) or a persisted gist surface
/// (PersistedSummary). Inner thoughts, world experiences, reflection
/// outputs, and context-only artifacts are ALLOWED to overlap with
/// contact text — that's normal interior recall.
///
/// **Behaviour when no contact context provided**: returns Pass — the
/// invariant cannot run without
/// <see cref="CognitiveArtifact.ContactRecentMessages"/>. This is a
/// soft-skip, not a fail; producers that don't supply context aren't
/// covered, but that's a producer-side gap, not an invariant failure.
/// </summary>
public sealed class AntiParrotInvariant : ICognitiveOutputInvariant
{
    public string Name => "anti-parrot";

    /// <summary>
    /// Token-count threshold for verbatim-lift detection. Sourced from
    /// <see cref="AntiParrotPromptFragments.VerbatimNGramThreshold"/> so
    /// V1.2's producer-side prompt-rule and this gate-side check use a
    /// single source of truth (J.5e consolidation, May 1, 2026).
    /// </summary>
    public const int VerbatimNGramThreshold = AntiParrotPromptFragments.VerbatimNGramThreshold;

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        // Dispatch + PersistedSummary are the surfaces where contact-verbatim
        // lifts cause harm. Inner thoughts and world experiences are creative
        // interior — overlap is allowed there.
        if (artifact.IntendedSink is not (CognitiveOutputSink.Dispatch
                                       or CognitiveOutputSink.PersistedSummary))
            return false;

        // Producer kinds that emit contact-facing or gist-surface output.
        return artifact.ProducerKind is
            CognitiveProducerKind.ConversationReply
         or CognitiveProducerKind.Outreach
         or CognitiveProducerKind.ClosedThreadSummary
         or CognitiveProducerKind.Voice;
    }

    public Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct)
    {
        if (artifact.ContactRecentMessages is null
            || artifact.ContactRecentMessages.Count == 0)
            return Task.FromResult(InvariantResult.Pass());

        var outputTokens = Tokenize(artifact.Content);
        if (outputTokens.Count < VerbatimNGramThreshold)
            return Task.FromResult(InvariantResult.Pass());

        foreach (var contactMsg in artifact.ContactRecentMessages)
        {
            if (string.IsNullOrWhiteSpace(contactMsg)) continue;
            var contactTokens = Tokenize(contactMsg);
            if (contactTokens.Count < VerbatimNGramThreshold) continue;

            var match = FindLongestVerbatimRun(outputTokens, contactTokens);
            if (match.Length >= VerbatimNGramThreshold)
            {
                var lifted = string.Join(' ', outputTokens.GetRange(match.Start, match.Length));
                var hint =
                    $"output contains {match.Length}-token verbatim lift from contact message: \"{Truncate(lifted, 80)}\"";
                return Task.FromResult(InvariantResult.Fail(hint));
            }
        }

        return Task.FromResult(InvariantResult.Pass());
    }

    /// <summary>
    /// Find the longest contiguous run of <paramref name="output"/>
    /// tokens that appears verbatim in <paramref name="contact"/>.
    /// Returns (start index in output, length). Length=0 if no run.
    /// O(n*m) sliding window; n + m bounded by tokens-per-message in
    /// practice (~100 tokens), so trivially fast.
    /// </summary>
    internal static (int Start, int Length) FindLongestVerbatimRun(
        IReadOnlyList<string> output, IReadOnlyList<string> contact)
    {
        var bestStart = 0;
        var bestLen = 0;
        for (var i = 0; i < output.Count; i++)
        {
            for (var j = 0; j < contact.Count; j++)
            {
                var run = 0;
                while (i + run < output.Count
                    && j + run < contact.Count
                    && string.Equals(output[i + run], contact[j + run], StringComparison.Ordinal))
                {
                    run++;
                }
                if (run > bestLen) { bestLen = run; bestStart = i; }
            }
        }
        return (bestStart, bestLen);
    }

    /// <summary>
    /// Lowercase-tokenise on whitespace + strip surrounding punctuation
    /// per token. Keeps tokens in original order so the recovered match
    /// renders sensibly. NOT the same tokeniser as
    /// <see cref="ClosedConversationSummarizer"/>'s topic-keyword
    /// tokeniser (that one is content-keyword-extraction-shaped, drops
    /// stopwords; this one preserves order + stopwords for verbatim
    /// matching).
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        var lower = text.ToLowerInvariant();
        var raw = lower.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<string>(raw.Length);
        foreach (var t in raw)
        {
            var stripped = t.Trim('.', ',', '!', '?', ';', ':', '"', '(', ')', '[', ']', '\'', '—', '–', '-');
            if (stripped.Length > 0) tokens.Add(stripped);
        }
        return tokens;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}
