using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 3 (2026-08-18) — singular surface for
/// classifying the SHAPE of an inner thought (as distinct from its
/// emotional REGISTER — that flows through <see cref="IRegisterClassifier"/>).
///
/// <para>
/// Shape captures whether the thought looks like a healthy first-person
/// interior monologue (<see cref="ThoughtShape.CoherentThought"/>) or one
/// of the empirically-observed pathologies:
/// <see cref="ThoughtShape.ThirdPersonFrame"/> (Mark reported from outside),
/// <see cref="ThoughtShape.FactCatalog"/> (enumeration / prompt-echo), or
/// <see cref="ThoughtShape.MumbleLoop"/> (verbatim self-repetition). Same
/// dedicated-classifier seam as <c>IRegisterClassifier</c>; deliberately
/// NOT folded into the existing hybrid metadata prompt so shape can be
/// tuned, swapped, or disabled independently.
/// </para>
///
/// <para>
/// See <c>ani-docs/spec/ANI-Foundation-Input-Refactor-Plan.md</c> §Phase 3
/// for scope and acceptance. Empirical shape distribution (2026-08-18 scan
/// of last 200 InnerThought memories) is captured in
/// <c>ThoughtShape</c> XML doc.
/// </para>
/// </summary>
public interface IThoughtShapeClassifier
{
    /// <summary>
    /// Classify the SHAPE of an inner-thought <paramref name="content"/>.
    /// </summary>
    /// <remarks>
    /// Failure contract: any transport / parse / timeout error is caught and
    /// returned as <see cref="ThoughtShape.Unclassified"/> with a WARN log.
    /// Consumers treat Unclassified as fail-open — the cognitive cycle proceeds
    /// using the thought text as-is, downstream instrumentation records the
    /// Unclassified count for observability.
    /// </remarks>
    Task<ThoughtShape> ClassifyAsync(string content, CancellationToken ct);
}
