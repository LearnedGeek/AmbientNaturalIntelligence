using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme O Phase O.1 (May 10, 2026) — middleware pipeline stage marker.
/// Pre handlers run BEFORE composition (frame selection, slice composition,
/// substrate prep). Post handlers run AFTER composition (gate invariants,
/// frame coherence, claim verification, direct address).
/// </summary>
public enum PipelineStage
{
    /// <summary>Runs before the producer's composition delegate is invoked.</summary>
    Pre  = 1,

    /// <summary>Runs after composition; receives the produced text via
    /// <see cref="Models.CognitivePipelineContext.ComposedContent"/> and
    /// <see cref="Models.CognitiveArtifact.Content"/>.</summary>
    Post = 2,
}

/// <summary>
/// Theme O Phase O.1 (May 10, 2026) — uniform handler interface for the
/// cognitive pipeline middleware. Each handler advertises a <see cref="Stage"/>
/// (Pre or Post), an <see cref="AppliesTo"/> predicate that decides whether
/// it fires for a given artifact (so a single pipeline can serve multiple
/// producer kinds — §9 lock 2), and a <see cref="HandleAsync"/> body.
///
/// Handlers can short-circuit the pipeline by returning
/// <see cref="HandlerResult.ShortCircuitWith"/>. The orchestrator captures the
/// short-circuit handler name + stage in the resulting
/// <see cref="DispatchResult"/> for telemetry.
///
/// **Stage = Pre** semantics: a Pre short-circuit prevents composition AND
/// all Post handlers from running. **Stage = Post** semantics: a Post
/// short-circuit prevents subsequent Post handlers from running, but
/// composition has already happened.
/// </summary>
public interface ICognitivePipelineHandler
{
    /// <summary>Which stage this handler runs at. Locked at construction.</summary>
    PipelineStage Stage { get; }

    /// <summary>
    /// Stable name for telemetry. Example: <c>"FrameSelection"</c>,
    /// <c>"SelfEchoInvariant"</c>. Surfaces in <c>O_HANDLER_*</c> log lines
    /// + <see cref="DispatchResult.ShortCircuitHandler"/> on short-circuit.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether this handler applies to the given artifact. Returning false
    /// causes the orchestrator to skip the handler — <see cref="HandleAsync"/>
    /// is NOT called. This is the §9 lock 2 mechanism for one-pipeline-serves-
    /// many-producers: a handler that only applies to Outreach returns false
    /// for ConversationReply artifacts.
    /// </summary>
    bool AppliesTo(CognitiveArtifact artifact);

    /// <summary>
    /// Run the handler against the pipeline context. Handlers should:
    /// (a) mutate <paramref name="ctx"/> properties or state when needed,
    /// (b) return <see cref="HandlerResult.Continue"/> on success, or
    /// (c) return <see cref="HandlerResult.ShortCircuitWith"/> to stop the pipeline.
    ///
    /// Handler exceptions are caught by the orchestrator and treated as a
    /// <see cref="OutputGateVerdict.Fail"/> short-circuit — observability bugs in
    /// handlers MUST NOT cause unhandled-exception crashes upstream.
    /// </summary>
    Task<HandlerResult> HandleAsync(CognitivePipelineContext ctx, CancellationToken ct);
}
