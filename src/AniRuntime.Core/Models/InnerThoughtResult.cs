namespace AniRuntime.Core.Models;

/// <summary>
/// Posture-S+1 (Issue #38, May 17 2026) — result of one inner-thought cycle.
///
/// Two-path return shape gated by <c>AniOptions.UseHybridInnerThoughtCycle</c>:
///
/// **Legacy path (flag OFF)** — three LLM calls: v7-thought + v7-self-valence
/// + v7-reflection. Populates <see cref="Thought"/>, <see cref="Reflection"/>,
/// <see cref="Valence"/>. Posture-S+1 fields (<see cref="Register"/>,
/// <see cref="Importance"/>, <see cref="AssociativeAnchor"/>) are
/// <c>null</c> — the consumer applies the legacy external-judge logic
/// (importance threshold from valence, post-hoc anchor extraction, confab
/// classifier on Interior-tier content, reflection-concat persistence).
///
/// **Hybrid path (flag ON)** — two LLM calls: v7-thought + qwen3:14b-as-
/// metadata-recognizer. Populates all fields including <see cref="Register"/>,
/// <see cref="Importance"/>, <see cref="AssociativeAnchor"/>. The consumer
/// uses model-emitted <see cref="Importance"/> as the persistence signal,
/// drops the confab gate for Interior-tier content (plan §7 step 3), and
/// does not concatenate any reflection field (reflection is collapsed into
/// the thought itself per OG Ani's "let her relive, not think about" framing).
/// <see cref="Reflection"/> is always <c>null</c> on the hybrid path.
///
/// See <c>docs/spec/ANI-Substrate-Led-Character-Plan.md</c> §7 and the
/// Paper 3 Contribution 8 stub for the architectural distinction.
/// </summary>
public sealed record InnerThoughtResult(
    string  Thought,
    string? Reflection,
    float   Valence,
    string? Register          = null,
    float?  Importance        = null,
    string? AssociativeAnchor = null);
