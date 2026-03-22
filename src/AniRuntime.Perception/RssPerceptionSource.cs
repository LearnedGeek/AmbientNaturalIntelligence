using System.Xml;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Perception;

/// <summary>
/// Polls configured RSS feeds and surfaces new items as perception events.
/// Gives Ani awareness of the outside world — things happening beyond her own thoughts.
/// Feeds should be curated to match her interests (books, food, culture, weather).
/// </summary>
public sealed class RssPerceptionSource : IPerceptionSource
{
    private readonly IHttpClientFactory             _httpFactory;
    private readonly IMemoryService                 _memory;
    private readonly IIntentExtractor               _intent;
    private readonly RssOptions                     _options;
    private readonly ILogger<RssPerceptionSource>   _log;

    // Track last-seen publish date per feed to avoid re-emitting old items
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new();

    // Cached keyword sets from CharacterStateDoc — refreshed lazily
    private List<string>? _relevanceKeywords;

    public string             SourceName => "rss";
    public PerceptionCategory Category   => PerceptionCategory.Content;
    public bool               IsEnabled  => _options.Enabled && _options.Feeds.Count > 0;

    // Cached interest descriptions for semantic relevance scoring
    private List<string>? _interestDescriptions;

    public RssPerceptionSource(
        IHttpClientFactory httpFactory,
        IMemoryService memory,
        IIntentExtractor intent,
        IOptions<RssOptions> options,
        ILogger<RssPerceptionSource> log)
    {
        _httpFactory = httpFactory;
        _memory      = memory;
        _intent      = intent;
        _options     = options.Value;
        _log         = log;
    }

    public async Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        // Lazy-load relevance keywords from character state
        if (_relevanceKeywords is null)
            await LoadRelevanceKeywordsAsync(ct).ConfigureAwait(false);

        var events = new List<PerceptionEvent>();

        foreach (var feed in _options.Feeds)
        {
            try
            {
                var items = await FetchFeedAsync(feed, since, ct).ConfigureAwait(false);
                events.AddRange(items);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "RSS feed '{Feed}' failed — skipping", feed.Name);
            }
        }

        if (events.Count > 0)
            _log.LogDebug("RSS perception: {Count} new items across {Feeds} feeds",
                events.Count, _options.Feeds.Count);

        return events;
    }

    private async Task<List<PerceptionEvent>> FetchFeedAsync(
        RssFeed feed, DateTimeOffset since, CancellationToken ct)
    {
        var cutoff = _lastSeen.TryGetValue(feed.Url, out var last) && last > since ? last : since;
        var events = new List<PerceptionEvent>();

        using var http     = _httpFactory.CreateClient("rss");
        using var response = await http.GetAsync(feed.Url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var doc = new XmlDocument();
        doc.Load(stream);

        // Support both RSS 2.0 (<item>) and Atom (<entry>)
        var items = doc.GetElementsByTagName("item");
        if (items.Count == 0)
            items = doc.GetElementsByTagName("entry");

        var newestSeen = cutoff;
        var count = 0;

        foreach (XmlNode item in items)
        {
            if (count >= _options.MaxItemsPerFeed) break;

            var title   = GetChildText(item, "title");
            var pubDate = ParsePubDate(item);

            if (string.IsNullOrWhiteSpace(title)) continue;
            if (pubDate.HasValue && pubDate.Value <= cutoff) continue;

            var description = GetChildText(item, "description")
                           ?? GetChildText(item, "summary")
                           ?? string.Empty;

            // Strip HTML tags from description for a clean summary
            description = StripHtml(description);
            if (description.Length > 150)
                description = description[..150] + "…";

            var summary = string.IsNullOrWhiteSpace(description)
                ? $"[{feed.Name}] {title}"
                : $"[{feed.Name}] {title} — {description}";

            // Two-pass relevance: cheap keyword pre-filter, then semantic scoring
            // for items that pass the keyword bar. This avoids an LLM call for
            // every RSS item while ensuring genuinely relevant items are scored accurately.
            var keywordScore = ScoreRelevance(title, description);
            var relevance = keywordScore;

            if (keywordScore >= 0.4f && _interestDescriptions is { Count: > 0 })
            {
                try
                {
                    relevance = await _intent.ScoreRelevanceAsync(
                        summary, _interestDescriptions, ct).ConfigureAwait(false);
                    _log.LogDebug("RSS semantic relevance: keyword={Keyword:F2} → semantic={Semantic:F2} for \"{Title}\"",
                        keywordScore, relevance, title.Length > 50 ? title[..50] + "..." : title);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "RSS semantic scoring failed — using keyword score {Score:F2}", keywordScore);
                }
            }

            events.Add(new PerceptionEvent
            {
                SourceName    = SourceName,
                Category      = Category,
                Summary       = summary,
                ContactRelevance = relevance,
                OccurredAt    = pubDate ?? DateTimeOffset.UtcNow,
            });

            if (pubDate.HasValue && pubDate.Value > newestSeen)
                newestSeen = pubDate.Value;

            count++;
        }

        _lastSeen[feed.Url] = newestSeen;
        return events;
    }

    private static string? GetChildText(XmlNode parent, string childName)
    {
        var child = parent[childName];
        return child?.InnerText?.Trim();
    }

    private static DateTimeOffset? ParsePubDate(XmlNode item)
    {
        // RSS 2.0: <pubDate>, Atom: <published> or <updated>
        var raw = GetChildText(item, "pubDate")
               ?? GetChildText(item, "published")
               ?? GetChildText(item, "updated");

        if (raw is not null && DateTimeOffset.TryParse(raw, out var parsed))
            return parsed;

        return null;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;

        // Simple tag stripping — good enough for RSS descriptions
        var inTag = false;
        var result = new System.Text.StringBuilder(html.Length);
        foreach (var ch in html)
        {
            switch (ch)
            {
                case '<': inTag = true; break;
                case '>': inTag = false; break;
                default:
                    if (!inTag) result.Append(ch);
                    break;
            }
        }
        return result.ToString().Trim();
    }

    // ── Relevance scoring ────────────────────────────────────────────────────

    /// <summary>
    /// Loads keywords from CharacterStateDoc — things Mark cares about, interests,
    /// shared experiences. These drive relevance scoring for RSS items.
    /// Extracted once and cached for the lifetime of the source.
    /// </summary>
    private async Task LoadRelevanceKeywordsAsync(CancellationToken ct)
    {
        try
        {
            var cs = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Extract meaningful keywords from character state lists
            foreach (var source in new[] { cs.ThingsContactCares, cs.Interests, cs.SharedExperiences })
            {
                foreach (var entry in source)
                {
                    // Split on common delimiters and take words 4+ chars (skip "the", "and", etc.)
                    var words = entry.Split(new[] { ' ', ',', '—', '–', '-', '/', '(', ')' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var word in words.Where(w => w.Length >= 4))
                        keywords.Add(word.TrimEnd('.', '!', '?', '\'', '"'));
                }
            }

            // Also add topic valence keys as-is (they're already curated phrases)
            foreach (var topic in cs.TopicValence.Keys)
                keywords.Add(topic.ToLowerInvariant());

            _relevanceKeywords = keywords.ToList();
            _log.LogDebug("Loaded {Count} relevance keywords from character state", _relevanceKeywords.Count);

            // Build semantic interest descriptions — full phrases for LLM relevance scoring.
            // These give the scorer context about WHY Mark cares, not just keyword matches.
            var descriptions = new List<string>();
            foreach (var entry in cs.ThingsContactCares)
                descriptions.Add($"Mark cares about: {entry}");
            foreach (var entry in cs.Interests)
                descriptions.Add($"Mark is interested in: {entry}");
            foreach (var entry in cs.SharedExperiences.Take(5))
                descriptions.Add($"Shared experience: {entry}");
            _interestDescriptions = descriptions;
            _log.LogDebug("Loaded {Count} interest descriptions for semantic scoring", descriptions.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load relevance keywords — using empty set");
            _relevanceKeywords = new List<string>();
            _interestDescriptions = new List<string>();
        }
    }

    /// <summary>
    /// Scores an RSS item's relevance to Mark based on keyword matches.
    /// Returns 0.2 (baseline) for no matches, scaling up to 0.85 for strong matches.
    /// Items above the reactive share threshold can trigger direct sharing.
    /// </summary>
    private float ScoreRelevance(string title, string description)
    {
        if (_relevanceKeywords is null || _relevanceKeywords.Count == 0)
            return 0.2f;

        var text = $"{title} {description}".ToLowerInvariant();
        var matchCount = _relevanceKeywords.Count(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase));

        return matchCount switch
        {
            0     => 0.2f,   // no match — generic background awareness
            1     => 0.4f,   // weak match — colors thoughts
            2     => 0.6f,   // moderate — worth noting
            >= 3  => 0.85f,  // strong — Mark would care about this
            _     => 0.2f,
        };
    }
}
