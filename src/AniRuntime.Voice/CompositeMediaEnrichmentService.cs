using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Voice;

/// <summary>
/// Composes multiple media enrichment services (voice audio, images) into a single
/// IMediaEnrichmentService. Each enrichment runs independently — one failing doesn't
/// prevent others from attaching. Twilio MMS supports multiple media per message.
/// </summary>
public class CompositeMediaEnrichmentService : IMediaEnrichmentService
{
    private readonly IEnumerable<IMediaEnrichmentService> _enrichments;

    public CompositeMediaEnrichmentService(IEnumerable<IMediaEnrichmentService> enrichments)
    {
        _enrichments = enrichments;
    }

    public async Task<List<Uri>> EnrichAsync(OutreachDecision decision, CancellationToken ct = default)
    {
        var allUrls = new List<Uri>();

        foreach (var enrichment in _enrichments)
        {
            // Skip self to prevent infinite recursion
            if (enrichment is CompositeMediaEnrichmentService) continue;

            try
            {
                var urls = await enrichment.EnrichAsync(decision, ct).ConfigureAwait(false);
                allUrls.AddRange(urls);
            }
            catch
            {
                // Individual enrichment failure is non-fatal — continue with others
            }
        }

        return allUrls;
    }
}
