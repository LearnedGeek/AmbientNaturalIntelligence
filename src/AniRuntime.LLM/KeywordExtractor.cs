using System.Text.RegularExpressions;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AniRuntime.LLM;

/// <summary>
/// TF-IDF keyword extraction for improving memory retrieval queries.
///
/// Problem: casual messages like "Hey babe! Did I tell you about my consulting business?"
/// embed poorly because greetings and endearments dominate the vector. The search returns
/// other casual messages instead of topic-specific memories.
///
/// Solution: extract the most distinctive words from the message using TF-IDF scoring
/// against the memory corpus. "consulting" and "business" score high (rare in corpus);
/// "hey", "babe", "today" score low (appear everywhere). The extracted keywords are
/// used as a secondary search query that finds topic-specific memories.
///
/// IDF is built lazily from the memory corpus on first use, then cached for the lifetime
/// of the service. At ~1000-2000 memories this takes milliseconds.
/// </summary>
public partial class KeywordExtractor
{
    private readonly IMemorySearch _search;
    private readonly ILogger<KeywordExtractor> _log;

    private Dictionary<string, float>? _idfCache;
    private int _corpusSize;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    public KeywordExtractor(IMemorySearch search, ILogger<KeywordExtractor> log)
    {
        _search = search;
        _log = log;
    }

    /// <summary>
    /// Extracts the most distinctive keywords from a message using TF-IDF scoring
    /// against the memory corpus. Returns keywords concatenated as a search query.
    /// </summary>
    public async Task<string?> ExtractSearchQueryAsync(string message, int topK = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length < 10)
            return null;

        await EnsureIdfBuiltAsync(ct).ConfigureAwait(false);

        if (_idfCache is null || _idfCache.Count == 0)
            return null;

        var words = Tokenize(message);
        if (words.Count == 0)
            return null;

        // Compute TF for this message
        var tf = new Dictionary<string, float>();
        foreach (var word in words)
        {
            tf.TryGetValue(word, out var count);
            tf[word] = count + 1;
        }

        // Normalize TF by message length
        var totalWords = (float)words.Count;
        foreach (var key in tf.Keys.ToList())
            tf[key] /= totalWords;

        // Score each word by TF * IDF
        var scored = new List<(string word, float tfidf)>();
        foreach (var (word, termFreq) in tf)
        {
            // Words not in the corpus get max IDF (they're unique/novel)
            var idf = _idfCache.GetValueOrDefault(word, _idfCache.Values.Max());
            scored.Add((word, termFreq * idf));
        }

        var topKeywords = scored
            .OrderByDescending(s => s.tfidf)
            .Take(topK)
            .Select(s => s.word)
            .ToList();

        if (topKeywords.Count == 0)
            return null;

        var query = string.Join(" ", topKeywords);
        _log.LogDebug("Keyword extraction: \"{Original}\" → \"{Keywords}\" (top {Count} by TF-IDF)",
            message.Length > 60 ? message[..60] + "..." : message, query, topKeywords.Count);

        return query;
    }

    /// <summary>
    /// Builds the IDF dictionary from the memory corpus. Called once lazily,
    /// then cached for the service lifetime.
    /// </summary>
    private async Task EnsureIdfBuiltAsync(CancellationToken ct)
    {
        if (_idfCache is not null)
            return;

        await _buildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_idfCache is not null)
                return;

            _log.LogDebug("Building IDF dictionary from memory corpus...");

            // Fetch all memory content — we only need text, not embeddings
            var episodic = await _search.GetByTypeAsync(Core.Models.MemoryType.Episodic, 500, ct).ConfigureAwait(false);
            var innerThoughts = await _search.GetByTypeAsync(Core.Models.MemoryType.InnerThought, 500, ct).ConfigureAwait(false);
            var semantic = await _search.GetByTypeAsync(Core.Models.MemoryType.Semantic, 200, ct).ConfigureAwait(false);

            var allDocs = episodic.Concat(innerThoughts).Concat(semantic).ToList();
            _corpusSize = allDocs.Count;

            if (_corpusSize == 0)
            {
                _idfCache = new Dictionary<string, float>();
                return;
            }

            // Document frequency: how many documents contain each word
            var df = new Dictionary<string, int>();
            foreach (var doc in allDocs)
            {
                var uniqueWords = new HashSet<string>(Tokenize(doc.Content));
                foreach (var word in uniqueWords)
                {
                    df.TryGetValue(word, out var count);
                    df[word] = count + 1;
                }
            }

            // IDF = log(N / df) — words appearing in every document get IDF ≈ 0
            _idfCache = new Dictionary<string, float>();
            foreach (var (word, docFreq) in df)
            {
                _idfCache[word] = MathF.Log((float)_corpusSize / docFreq);
            }

            _log.LogInformation("IDF dictionary built: {Words} unique words from {Docs} documents", _idfCache.Count, _corpusSize);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>
    /// Tokenizes text into lowercase words, stripping punctuation and very short words.
    /// Uses a simple regex approach — no external NLP library needed.
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        var words = WordPattern().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length >= 3)          // skip "a", "an", "is", etc.
            .Where(w => !IsStopWord(w))         // standard English stop words only
            .ToList();

        return words;
    }

    /// <summary>
    /// Minimal English stop word list — grammatical function words only.
    /// NOT content words, NOT endearments, NOT domain-specific terms.
    /// These words carry no semantic meaning in any context.
    /// IDF handles domain-specific filtering (e.g., "babe" appears in most
    /// messages → low IDF → filtered by scoring, not by this list).
    /// </summary>
    private static bool IsStopWord(string word)
    {
        return word switch
        {
            // Articles and determiners
            "the" or "this" or "that" or "these" or "those" => true,
            // Pronouns
            "you" or "your" or "yours" or "she" or "her" or "hers" or
            "him" or "his" or "its" or "they" or "them" or "their" or
            "our" or "ours" or "who" or "whom" or "which" or "what" => true,
            // Prepositions and conjunctions
            "and" or "but" or "for" or "nor" or "yet" or "with" or
            "from" or "into" or "onto" or "about" or "between" or
            "through" or "during" or "before" or "after" or "above" or
            "below" or "than" or "then" or "because" or "since" or
            "while" or "although" or "though" or "unless" or "until" => true,
            // Common verbs (auxiliary / copular)
            "are" or "was" or "were" or "been" or "being" or
            "have" or "has" or "had" or "having" or "does" or "did" or
            "doing" or "would" or "could" or "should" or "might" or
            "shall" or "will" or "can" or "may" or "must" => true,
            // Adverbs and misc function words
            "not" or "nor" or "very" or "really" or "just" or "also" or
            "too" or "much" or "more" or "most" or "some" or "any" or
            "all" or "each" or "every" or "both" or "few" or "many" or
            "such" or "only" or "own" or "same" or "other" or
            "how" or "when" or "where" or "why" or "here" or "there" => true,
            _ => false
        };
    }

    /// <summary>
    /// Invalidates the cached IDF dictionary, forcing a rebuild on next use.
    /// Call after significant corpus changes (e.g., bulk import).
    /// </summary>
    public void InvalidateCache() => _idfCache = null;

    [GeneratedRegex(@"[a-z']+")]
    private static partial Regex WordPattern();
}
