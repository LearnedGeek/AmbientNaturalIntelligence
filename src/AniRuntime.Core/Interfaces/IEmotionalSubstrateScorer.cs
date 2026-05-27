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
/// <see cref="EmotionVector"/> with schema-flexible axes (e.g. EmoLLaMA-
/// chat-7B contributes <c>anger / fear / joy / sadness / valence</c>;
/// future models can contribute additional axes such as <c>arousal</c>,
/// <c>severity</c>, etc. without breaking this interface).
/// </para>
///
/// <para>
/// Consumers — inner-thought cycle, outreach, conversation, vibe loop,
/// dashboard, voice-tag selector — read the produced vector via
/// <see cref="EmotionVectorProjector"/> projections. No consumer calls
/// this interface directly outside the contribution-write path.
/// </para>
///
/// <para>
/// Empirical case for replacing the existing discrete classifier paths:
/// #67 5-axis validation chain. Architectural design: #68.
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
    /// returning a zero vector with the same schema).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="EmotionVector"/> whose
    /// <see cref="EmotionVector.Components"/> dictionary contains axis
    /// values in [0.0, 1.0], keyed by lowercased axis name (use
    /// <see cref="EmotionAxis"/> + <see cref="EmotionAxisExtensions.Key"/>
    /// for standard axes).</returns>
    Task<EmotionVector> ScoreAsync(string text, CancellationToken ct);
}
