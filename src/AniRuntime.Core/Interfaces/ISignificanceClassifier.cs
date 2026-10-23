namespace AniRuntime.Core.Interfaces;

/// <summary>
/// F-4 Phase 4a.1b (2026-10-23) — classifies a Perception event's
/// objective-world-events significance on a 0.0–1.0 scale, independent
/// of any user's interest-match. Used at RSS ingest to decouple axis 1
/// of the three-dimension importance model (significance × preference ×
/// fatigue) from Mark-interest-match (<c>ContactRelevance</c>, which is
/// axis 2's seed).
///
/// <para>
/// <b>Taxonomy pinned by the implementation's prompt:</b> war /
/// mass violence / major political transition rank high (≥ 0.7);
/// celebrity gossip / minor sports / recipe rank low (≤ 0.3); most
/// news lands in the middle. The classifier is model-based (qwen3:14b
/// via <see cref="IOllamaClient.ChatJsonWithModelAsync"/>), same shape
/// as <see cref="IRegisterClassifier"/>.
/// </para>
///
/// <para>
/// <b>Failure contract:</b> any transport / parse / timeout error
/// returns the configured default (0.5) with a WARN log. Callers treat
/// this as fail-open — ingest still succeeds, the record is persisted
/// with Significance 0.5, retrieval ranking still works (just loses
/// the discriminator for this specific record).
/// </para>
///
/// <para>See issue #168 and
/// <c>ani-docs/research/ANI-Content-Coverage-Audit-2026-10-22.md</c>.</para>
/// </summary>
public interface ISignificanceClassifier
{
    /// <summary>
    /// Classify the objective world-events significance of
    /// <paramref name="eventSummary"/> (the headline / title / short
    /// description of an RSS item or other Perception event). Returns a
    /// clamped-to-[0.0, 1.0] value; fail-open default on error.
    /// </summary>
    Task<float> ClassifyAsync(string eventSummary, CancellationToken ct);
}
