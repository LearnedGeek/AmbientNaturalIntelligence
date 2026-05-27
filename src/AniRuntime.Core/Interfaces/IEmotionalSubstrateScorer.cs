using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Contribution 9 (Issue #68, 2026-05-27) — single substrate-measurement
/// producer for the runtime. Replaces the two divergent Ollama-via-prompt
/// classifier paths flagged in #66 (the per-contribution call in
/// <c>EmotionalProcessor</c> and the per-turn call in
/// <c>ClosedConversationSummarizer</c>) with one continuous-vector source
/// of truth.
///
/// <para>
/// Producer/consumer separation: this interface is the SINGLE place that
/// asks "what does this text feel like?" Implementations produce an
/// <see cref="EmotionVector"/> with schema-flexible axes carried in the
/// vector's dictionary, not the type structure. The production scorer
/// (PR-2) targets EmoLLaMA-chat-7B with EI-reg and E-c combined, emitting
/// namespaced keys: <c>ei.{emotion}</c> for EI-reg regression intensities,
/// <c>ec.{emotion}</c> for E-c multilabel presence, and <c>dim.{feature}</c>
/// for V-reg dimensional features. Future scorers can introduce additional
/// axes (arousal, dominance, severity) without breaking this interface or
/// any consumer that doesn't need the new axes.
/// </para>
///
/// <para>
/// Consumers (inner-thought cycle, outreach, conversation, vibe loop,
/// dashboard, voice-tag selector) read the produced vector through a
/// projection helper introduced in a follow-on PR. Projection rules
/// (mapping a vector to a 9-register taxonomy label, a 12-family
/// composition family, dashboard delta values) are deferred to a separate
/// design conversation because the projection rules must not be
/// hardcoded constants embedded in production code; they belong in
/// configuration so that adding or tuning a register taxonomy does not
/// require a code change. No consumer calls this interface directly
/// outside the contribution-write path.
/// </para>
///
/// <para>
/// Fractal-seed constraint: the substrate's axis count is the upper bound
/// on what every downstream projection can ever discriminate. EmoLLaMA's
/// EI-reg covers four categorical emotions (anger, fear, joy, sadness) and
/// V-reg covers valence; the seven additional E-c-only emotions
/// (anticipation, disgust, love, optimism, pessimism, surprise, trust)
/// arrive as binary presence rather than gradient intensity. Distinctions
/// the substrate cannot represent cannot be recovered downstream. Choosing
/// the substrate is the highest-leverage architectural decision in the
/// affective layer.
/// </para>
///
/// <para>
/// Empirical case for replacing the existing discrete classifier paths:
/// #67 5-axis validation chain. Architectural design: #68. Production-data
/// evaluation: `papers-short/emollm-production-eval/`.
/// </para>
/// </summary>
public interface IEmotionalSubstrateScorer
{
    /// <summary>
    /// Score the given text and return a continuous-vector emotion
    /// measurement. Implementations should populate the
    /// <see cref="EmotionVector.MeasurementSchema"/> field with a stable
    /// identifier so consumers can reason about substrate compatibility
    /// across model swaps (e.g. <c>"emollama-chat-7b-v1"</c>).
    /// </summary>
    /// <param name="text">The source content to score. Implementations
    /// must handle empty/whitespace input gracefully (typically by
    /// returning a vector with neutral / zero values).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="EmotionVector"/> whose
    /// <see cref="EmotionVector.Components"/> dictionary contains axis
    /// values in [0.0, 1.0], keyed by namespaced axis name (e.g.
    /// <c>"ei.anger"</c>, <c>"ec.surprise"</c>, <c>"dim.valence"</c>).
    /// Consumers using <see cref="EmotionAxis"/> or
    /// <see cref="DimensionAxis"/> enums construct the namespaced key
    /// themselves via the task-prefix convention.</returns>
    Task<EmotionVector> ScoreAsync(string text, CancellationToken ct);
}
