using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Voice;

/// <summary>
/// In-memory cache for media files (audio, images) that need to be served via HTTP
/// for Twilio MMS delivery. Entries expire after a configurable TTL.
///
/// Flow: synthesize audio → cache it → get public URL → attach to MMS → Twilio fetches it → entry expires.
/// </summary>
public class MediaCacheService
{
    private readonly ConcurrentDictionary<string, CachedMedia> _cache = new();
    private readonly ILogger<MediaCacheService> _logger;
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(10);

    public MediaCacheService(ILogger<MediaCacheService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Store media bytes and return the cache key (used in the serving URL).
    /// </summary>
    public string Store(byte[] data, string contentType)
    {
        Cleanup();

        var key = Guid.NewGuid().ToString("N");
        _cache[key] = new CachedMedia
        {
            Data        = data,
            ContentType = contentType,
            ExpiresAt   = DateTimeOffset.UtcNow + _ttl,
        };

        _logger.LogDebug("Media cached: {Key} ({Bytes} bytes, {ContentType}, expires {Expiry:HH:mm})",
            key, data.Length, contentType, _cache[key].ExpiresAt);

        return key;
    }

    /// <summary>
    /// Retrieve cached media by key. Returns null if expired or not found.
    /// </summary>
    public CachedMedia? Get(string key)
    {
        if (!_cache.TryGetValue(key, out var entry))
            return null;

        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            _cache.TryRemove(key, out _);
            return null;
        }

        return entry;
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _cache)
        {
            if (now > kvp.Value.ExpiresAt)
                _cache.TryRemove(kvp.Key, out _);
        }
    }

    public class CachedMedia
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = "application/octet-stream";
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
