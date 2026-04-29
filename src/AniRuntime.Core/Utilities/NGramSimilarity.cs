using System.Text.RegularExpressions;

namespace AniRuntime.Core.Utilities;

/// <summary>
/// Token-trigram similarity utility used to detect generation-loop degeneracy
/// pre-save (Theme E #5, Apr 29 2026). The cognitive cycle's inner-thought save
/// path can amplify recurrent thoughts by retrieval-feeding the next cycle the
/// thoughts it just stored — the duck-norris-77x / vanilla-cream-soda-30x
/// patterns surfaced in the Apr 28 conflation audit. Skipping the save on
/// near-duplicate content interrupts the loop at the persistence boundary
/// without needing to retrain or re-prompt.
///
/// Trigrams over word tokens (lowercase, punctuation stripped). Jaccard
/// similarity over the trigram sets. The default threshold of 0.50 means
/// "more than half of the trigrams overlap" — empirically the line between
/// "natural register continuity" (a writer revisiting a topic) and
/// "generation-loop degeneracy" (the model regurgitating the same thought
/// shape).
/// </summary>
public static class NGramSimilarity
{
    private static readonly Regex TokenSplit = new(@"[^\w']+", RegexOptions.Compiled);

    /// <summary>
    /// Tokenize text into lowercase word tokens. Punctuation is treated as a
    /// separator. Apostrophes inside words ("don't", "i'm") are preserved.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        return TokenSplit
            .Split(text.ToLowerInvariant())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
    }

    /// <summary>
    /// Build the set of token trigrams (3-word sliding windows) for a string.
    /// Returns an empty set if fewer than 3 tokens.
    /// </summary>
    public static HashSet<string> Trigrams(string? text)
    {
        var tokens = Tokenize(text);
        var set = new HashSet<string>();
        if (tokens.Count < 3) return set;

        for (var i = 0; i <= tokens.Count - 3; i++)
            set.Add($"{tokens[i]} {tokens[i + 1]} {tokens[i + 2]}");

        return set;
    }

    /// <summary>
    /// Jaccard similarity between two token-trigram sets:
    /// |A ∩ B| / |A ∪ B|. Returns 0.0 for empty sets (cannot compare).
    /// Range: [0.0, 1.0]. Higher = more similar.
    /// </summary>
    public static double JaccardSimilarity(string? textA, string? textB)
    {
        var a = Trigrams(textA);
        var b = Trigrams(textB);

        if (a.Count == 0 || b.Count == 0) return 0.0;

        var intersection = a.Intersect(b).Count();
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>
    /// True if the candidate text is a near-duplicate of any of the references
    /// at or above the threshold. Default 0.50 = more than half of the
    /// candidate's trigrams overlap with at least one reference. Used at
    /// inner-thought save time to skip near-duplicates that would otherwise
    /// re-feed the next cycle's retrieval pool.
    /// </summary>
    public static bool IsNearDuplicate(string candidate, IEnumerable<string> references, double threshold = 0.50)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var candidateTrigrams = Trigrams(candidate);
        if (candidateTrigrams.Count == 0) return false;

        foreach (var reference in references)
        {
            var referenceTrigrams = Trigrams(reference);
            if (referenceTrigrams.Count == 0) continue;

            var intersection = candidateTrigrams.Intersect(referenceTrigrams).Count();
            var union = candidateTrigrams.Count + referenceTrigrams.Count - intersection;
            var jaccard = union == 0 ? 0.0 : (double)intersection / union;
            if (jaccard >= threshold) return true;
        }

        return false;
    }
}
