using AniRuntime.Core.Interfaces;

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
///
/// <para>
/// F-1 Phase 3 (2026-08-18) — implements <see cref="IThoughtEnvelope"/>.
/// <see cref="Shape"/> is populated by <c>InnerThoughtPhase</c> via
/// <see cref="IThoughtShapeClassifier"/>; consumers can read either the
/// concrete field or the envelope interface. See F-1 Phase 3 sub-tasks in
/// <c>ani-docs/spec/ANI-Foundation-Input-Refactor-Plan.md</c>.
/// </para>
/// </summary>
public sealed record InnerThoughtResult(
    string                        Thought,
    string?                       Reflection,
    float                         Valence,
    string?                       Register          = null,
    float?                        Importance        = null,
    string?                       AssociativeAnchor = null,
    ThoughtShape                  Shape             = ThoughtShape.Unclassified,
    IReadOnlyList<ContentClaim>?  Claims            = null) : IThoughtEnvelope
{
    // ── IThoughtEnvelope / IProvenancedContent<string> ─────────────────────
    // Computed accessors — no additional persisted fields required.

    /// <inheritdoc />
    string IProvenancedContent<string>.Content => Thought;

    /// <inheritdoc />
    string IProvenancedContent<string>.SourceType => Shape switch
    {
        ThoughtShape.CoherentThought  => "thought.coherent-thought",
        ThoughtShape.ThirdPersonFrame => "thought.third-person-frame",
        ThoughtShape.FactCatalog      => "thought.fact-catalog",
        ThoughtShape.MumbleLoop       => "thought.mumble-loop",
        _                             => "thought.unclassified",
    };

    /// <inheritdoc />
    string IProvenancedContent<string>.Producer => "InnerThoughtPhase";

    /// <inheritdoc />
    /// <remarks>
    /// Captured ONCE at construction — see <c>IProvenancedContent&lt;T&gt;.CreatedAt</c>
    /// contract: "UTC timestamp when the envelope was CREATED by the
    /// producer." Every other implementor (<c>DesireTrigger</c>,
    /// <c>MemoryRecord</c>, <c>EmotionalContribution</c>, <c>MemoryLink</c>,
    /// <c>OpenLoop</c>) stores at construction; matching that discipline
    /// here keeps timestamp-based dedup / staleness / ordering stable
    /// against repeated envelope reads. Reviewer-caught (Devin / Serge /
    /// github-actions) on PR #112, fixed 2026-08-18.
    /// </remarks>
    DateTimeOffset IProvenancedContent<string>.CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    float[]? IProvenancedContent<string>.SemanticKey => null;
}
