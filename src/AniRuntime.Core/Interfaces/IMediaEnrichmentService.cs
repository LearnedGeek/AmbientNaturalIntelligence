using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Enriches an outgoing message with media attachments (voice audio, images, etc.).
/// Called at dispatch time, before Twilio sends the message.
/// Returns media URLs to attach as MMS content alongside the text.
/// </summary>
public interface IMediaEnrichmentService
{
    Task<List<Uri>> EnrichAsync(OutreachDecision decision, CancellationToken ct = default);
}
