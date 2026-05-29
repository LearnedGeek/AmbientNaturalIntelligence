namespace AniRuntime.Core.Models;

/// <summary>
/// Theme M (May 2026) — the read-only conscious-substrate gist computed at
/// prompt-build time and injected into composition prompts.
///
/// **Architectural property — read-only at inference (Theme M plan §3):** the
/// gist is generated, attached to a prompt-build call, and discarded. It is
/// never persisted to <see cref="MemoryRecord"/>, never appears in
/// retrieval pools, never enters the <c>conversation_messages</c> table.
/// Persisting would create an own-output recursive loop on the conscious-
/// substrate layer mirroring the §5.24 cascade we already have on the
/// Episodic layer; the entire architectural premise of Theme M collapses
/// if the gist itself becomes canonical substrate.
///
/// **M.0 status:** the no-op composer returns <see cref="Empty"/>. M.1
/// onward populates <see cref="Composed"/> with slice content per Theme M
/// plan §4. Token budgets and slice ordering live in
/// <c>AniOptions.ConsciousSubstrateGist*</c> settings.
///
/// **Telemetry:** every composition emits <c>M0_GIST_COMPOSITION</c> with
/// slice presence + token counts + total. Substrate-source ratio
/// (<c>M0_GIST_SUBSTRATE_RATIO</c>) is computed at the consumer surface
/// (the prompt builder) since it depends on retrieval / character-seed
/// sizes that the composer does not see.
/// </summary>
public sealed record ConsciousSubstrateGist
{
    /// <summary>
    /// The composed gist text, or empty when no slice is active.
    /// Maximum length bounded by <c>AniOptions.ConsciousSubstrateGistMaxTokens</c>
    /// (default 200 tokens). Token-counted via the same heuristic the prompt
    /// builder uses for retrieval blocks.
    /// </summary>
    public string Composed { get; init; } = string.Empty;

    /// <summary>
    /// Per-slice presence flags for telemetry. M.0 emits an all-false
    /// composition (no slices active); M.1 onward sets the corresponding
    /// flag when each slice is included.
    /// </summary>
    public GistSliceFlags Slices { get; init; } = new();

    /// <summary>
    /// Theme M Phase M.2-lite (May 28, 2026) — per-slice token counts for
    /// telemetry. Lets <c>M0_GIST_COMPOSITION</c> log line surface the
    /// token-share of each slice so substrate-thinness diagnosis can see
    /// WHERE the tokens go, not just the total. Required to measure
    /// M.6a / M.3 / M.4 / M.5 impact as those slices land.
    /// </summary>
    public GistSliceTokens SliceTokens { get; init; } = new();

    /// <summary>
    /// Approximate token count of <see cref="Composed"/>. Used for
    /// <c>M0_GIST_SUBSTRATE_RATIO</c> telemetry and budget enforcement.
    /// </summary>
    public int TokenCount { get; init; }

    /// <summary>The empty gist returned by the M.0 no-op composer.</summary>
    public static ConsciousSubstrateGist Empty { get; } = new();

    /// <summary>True when no slice contributed content — e.g. M.0 default
    /// state or feature-flag-disabled state.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Composed) && TokenCount == 0;
}

/// <summary>
/// Per-slice presence flags for the conscious-substrate gist (Theme M §4).
/// M.0 ships all-false; subsequent phases set flags as slices come online.
/// </summary>
public sealed record GistSliceFlags
{
    /// <summary>§4.1 Vibe Loop closed-conversation gist slice — active in M.3+.</summary>
    public bool ClosedConversation { get; init; }

    /// <summary>§4.2 Recent inner-thought aggregate slice — active in M.5+.</summary>
    public bool InnerThoughtAggregate { get; init; }

    /// <summary>§4.3 Display Rule register-state slice — active in M.1+ (with §4.8).</summary>
    public bool RegisterState { get; init; }

    /// <summary>§4.4 Contact-state perception aggregate slice — active in M.4+.</summary>
    public bool ContactState { get; init; }

    /// <summary>§4.5 World-Layer self-state slice — active in M.6+ (conditionally).</summary>
    public bool WorldSelf { get; init; }

    /// <summary>§4.8 Tension-state slice — active in M.1+ (with §4.3).</summary>
    public bool TensionState { get; init; }
}

/// <summary>
/// Per-slice approximate token counts for the conscious-substrate gist
/// (Theme M Phase M.2-lite, May 28, 2026). M.0/M.1 default to all-zero;
/// composer populates per-slice counts as it composes. Used by
/// <c>M0_GIST_COMPOSITION</c> telemetry to surface token-share by slice
/// so substrate-thinness diagnosis can see WHERE the tokens go.
/// </summary>
public sealed record GistSliceTokens
{
    public int ClosedConversation { get; init; }
    public int InnerThoughtAggregate { get; init; }
    public int RegisterState { get; init; }
    public int ContactState { get; init; }
    public int WorldSelf { get; init; }
    public int TensionState { get; init; }
}
