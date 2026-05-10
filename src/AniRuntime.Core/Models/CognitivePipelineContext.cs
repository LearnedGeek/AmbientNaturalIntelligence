namespace AniRuntime.Core.Models;

/// <summary>
/// Theme O Phase O.1 (May 10, 2026) — the shared mutable state that flows
/// through the cognitive pipeline. Lock §9.4 (May 10 18:06 CDT): typed
/// properties for load-bearing common data; type-keyed state-bag for
/// handler-extensible state. NO generic-object Bag; NO chained
/// handler-return objects.
///
/// Pre handlers populate <see cref="Frame"/> and may stash structured
/// telemetry via <see cref="SetState{T}"/>. Composition is the implicit
/// middle stage — the orchestrator writes <see cref="ComposedContent"/> and
/// updates <see cref="CognitiveArtifact.Content"/> on the way through.
/// Post handlers read both fields plus anything Pre handlers stashed in
/// state.
///
/// **Type-keyed state**: handlers communicate strongly-typed values via
/// the internal <c>IDictionary&lt;Type, object&gt;</c>. Handler A:
/// <c>ctx.SetState(new FrameSelectionTelemetry(...))</c>. Handler B:
/// <c>var tel = ctx.GetState&lt;FrameSelectionTelemetry&gt;()</c>. No
/// string keys, no object casts, no fishing.
/// </summary>
public sealed class CognitivePipelineContext
{
    /// <summary>
    /// The artifact the pipeline is operating on. Pre handlers may rewrite
    /// fields on it (e.g. attach <see cref="CognitiveArtifact.Frame"/>);
    /// composition updates <see cref="CognitiveArtifact.Content"/>. Setter
    /// allowed because some handlers may need to wrap the artifact (rare,
    /// but reserved).
    /// </summary>
    public CognitiveArtifact Artifact { get; set; } = default!;

    /// <summary>
    /// The context snapshot built by ContextBuilder for this cognitive cycle.
    /// Read-only — never mutated by pipeline handlers. Init-only enforces
    /// that.
    /// </summary>
    public ContextSnapshot Snapshot { get; init; } = default!;

    /// <summary>
    /// The selected source-frame, when a Pre handler has populated it.
    /// Mutable: the frame-selection pre-stage handler writes this; the
    /// frame-coherence post-stage invariant reads it (via the artifact's
    /// own <see cref="CognitiveArtifact.Frame"/>, which the producer copies
    /// from here before dispatch).
    /// </summary>
    public OutreachFrame? Frame { get; set; }

    /// <summary>
    /// The text produced by the composition delegate. Null before the
    /// composition stage runs; populated by the orchestrator. Same value is
    /// also assigned to <see cref="CognitiveArtifact.Content"/>.
    /// </summary>
    public string? ComposedContent { get; set; }

    /// <summary>Walltime when the orchestrator began this RunAsync call.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    // ── Type-keyed state bag ──────────────────────────────────────────────
    private readonly Dictionary<Type, object> _state = new();

    /// <summary>
    /// Read a previously-set state value by type. Returns null if no value
    /// of type <typeparamref name="T"/> has been set.
    /// </summary>
    public T? GetState<T>() where T : class
    {
        return _state.TryGetValue(typeof(T), out var value) ? (T)value : null;
    }

    /// <summary>
    /// Write a state value keyed by its concrete type. Overwrites any
    /// previous value of the same type. Null <paramref name="state"/>
    /// throws — use a distinct sentinel type if you need to signal absence.
    /// </summary>
    public void SetState<T>(T state) where T : class
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        _state[typeof(T)] = state;
    }
}
