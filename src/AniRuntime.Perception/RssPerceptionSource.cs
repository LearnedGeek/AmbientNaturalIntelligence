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
    private readonly RssOptions                     _options;
    private readonly ILogger<RssPerceptionSource>   _log;

    // Track last-seen publish date per feed to avoid re-emitting old items
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new();

    public string             SourceName => "rss";
    public PerceptionCategory Category   => PerceptionCategory.Content;
    public bool               IsEnabled  => _options.Enabled && _options.Feeds.Count > 0;

    public RssPerceptionSource(
        IHttpClientFactory httpFactory,
        IOptions<RssOptions> options,
        ILogger<RssPerceptionSource> log)
    {
        _httpFactory = httpFactory;
        _options     = options.Value;
        _log         = log;
    }

    public async Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
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

            events.Add(new PerceptionEvent
            {
                SourceName    = SourceName,
                Category      = Category,
                Summary       = summary,
                MarkRelevance = 0.2f,
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
}
