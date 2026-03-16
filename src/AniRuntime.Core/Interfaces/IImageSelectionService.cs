using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Selects an image from the curated library based on message context and emotional state.
/// Returns null when no image is appropriate (no match, rate limit, probability gate).
/// </summary>
public interface IImageSelectionService
{
    /// <summary>
    /// Select an image that matches the message context and current emotional state.
    /// Returns the local file path of the selected image, or null if no image should be sent.
    /// </summary>
    Task<string?> SelectImageAsync(string messageContext, EmotionalState emotionalState, CancellationToken ct = default);
}
