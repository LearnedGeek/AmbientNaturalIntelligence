namespace AniRuntime.LLM;

/// <summary>
/// Detects verbatim phrase reuse between two strings ("parroting").
/// Replaces cosine-similarity-as-parroting-proxy, which measures topical
/// overlap rather than actual phrase reuse.
///
/// Context (Pipeline Audit Section 8.3, April 2026): the prior echo guard
/// used cosine similarity against embeddings to detect when a reply was
/// "echoing" the incoming message. Cosine measures topical overlap —
/// shared vocabulary — which is EXPECTED for a reply engaging with its
/// prompt. A reply that says "good morning, coffee ritual, gym prep"
/// in response to a message that said the same things is on-topic
/// engagement, not parroting. The cosine guard was false-positiving on
/// legitimate conversational behavior.
///
/// Parroting is a different signal: verbatim reuse of specific phrases.
/// "Good morning, hope you slept well" → "Good morning, hope YOU slept well
/// too!" is parroting. "Good morning, coffee is already betraying me" is
/// engagement. The first shares a contiguous 4-gram verbatim; the second
/// shares vocabulary but no phrase.
///
/// Measurement:
///   - tokenize both strings on whitespace + lowercase normalization
///   - strip punctuation for comparison purposes
///   - find the longest shared contiguous n-gram
///   - return length (in tokens) and a normalized score
///
/// This detector is not perfect — it doesn't catch paraphrased parroting
/// (e.g., one side says "good morning" and the other says "morning's
/// good"). For the conversation-reply use case those cases are rare and
/// are also legitimately engagement behavior. We are explicitly optimizing
/// to NOT fire on topical overlap, which means accepting occasional misses
/// on heavily-paraphrased mimicry. That tradeoff is consistent with the
/// audit's architectural direction: trust the model's conversational
/// competence; only fire on behaviorally clear failures.
/// </summary>
public static class ParrotingDetector
{
    /// <summary>
    /// Default threshold for declaring parroting. An n-gram of 5+ tokens
    /// shared verbatim is a strong signal of reuse. Tuned to be tolerant
    /// of short-phrase coincidences ("the gym", "this morning") while
    /// catching near-verbatim mirroring.
    /// </summary>
    public const int DefaultMinNGramTokens = 5;

    /// <summary>
    /// Checks whether <paramref name="reply"/> parrots
    /// <paramref name="prior"/> — defined as sharing a contiguous
    /// n-gram of at least <paramref name="minNGramTokens"/> tokens after
    /// case + punctuation normalization.
    /// </summary>
    /// <returns>
    /// True if a shared n-gram of the required size exists. Also returns
    /// the longest shared n-gram (in tokens) for diagnostics.
    /// </returns>
    public static (bool IsParroting, int LongestSharedNGram, string? SharedPhrase) Check(
        string reply, string prior, int minNGramTokens = DefaultMinNGramTokens)
    {
        if (string.IsNullOrWhiteSpace(reply) || string.IsNullOrWhiteSpace(prior))
            return (false, 0, null);

        var replyTokens = Tokenize(reply);
        var priorTokens = Tokenize(prior);

        if (replyTokens.Length == 0 || priorTokens.Length == 0)
            return (false, 0, null);

        // Longest common contiguous substring on the token sequence.
        // Classic DP — O(m*n) time, O(min(m,n)) space with the rolling row.
        // Both strings are small (replies + prior messages are bounded by
        // conversational length), so this is trivially fast.
        var m = replyTokens.Length;
        var n = priorTokens.Length;

        var prev = new int[n + 1];
        var curr = new int[n + 1];

        var bestLen = 0;
        var bestEndInReply = -1;

        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                if (replyTokens[i - 1] == priorTokens[j - 1])
                {
                    curr[j] = prev[j - 1] + 1;
                    if (curr[j] > bestLen)
                    {
                        bestLen = curr[j];
                        bestEndInReply = i - 1;
                    }
                }
                else
                {
                    curr[j] = 0;
                }
            }

            (prev, curr) = (curr, prev);
            Array.Clear(curr, 0, curr.Length);
        }

        if (bestLen < minNGramTokens)
            return (false, bestLen, null);

        var start = bestEndInReply - bestLen + 1;
        var sharedPhrase = string.Join(' ', replyTokens.AsSpan(start, bestLen).ToArray());
        return (true, bestLen, sharedPhrase);
    }

    /// <summary>
    /// Case-lowers and strips outer punctuation, then splits on whitespace.
    /// Internal punctuation (apostrophes, hyphens) is preserved because it
    /// changes identity of the token ("don't" vs "dont" shouldn't match).
    /// </summary>
    private static string[] Tokenize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return Array.Empty<string>();

        var raw = input.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        var result = new string[raw.Length];
        var outIdx = 0;
        foreach (var t in raw)
        {
            var trimmed = t.Trim('.', ',', '!', '?', ';', ':', '"', '(', ')', '[', ']', '{', '}', '—', '–');
            if (trimmed.Length > 0)
                result[outIdx++] = trimmed;
        }

        if (outIdx == raw.Length) return result;

        var compacted = new string[outIdx];
        Array.Copy(result, compacted, outIdx);
        return compacted;
    }
}
