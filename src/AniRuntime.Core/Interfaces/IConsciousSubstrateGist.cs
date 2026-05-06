using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme M (May 2026) — the conscious-substrate gist composer.
///
/// **What this is:** the producer of the read-only generated gist
/// described in Theme M plan §3. Composes 1–6 slices (per §4.1–§4.8)
/// into a single short prompt-block at composition time, injected into
/// <c>BuildReplyPromptAsync</c> (M.1+) and <c>BuildOutreachCompositionPrompt</c>
/// (M.4+) behind the <c>ConsciousSubstrateGistEnabled</c> feature flag.
///
/// **Architectural property — read-only at inference:** implementations
/// MUST NOT call <see cref="IMemoryService"/>.SaveAsync, MUST NOT call
/// <see cref="IConversationService"/>.AddMessageAsync, and MUST NOT
/// produce side effects that cause the gist content to enter retrieval
/// pools (Mem0 / A-MEM / closed_conversation_records). Pinned by spec
/// tests in M.0 (`ConsciousSubstrateGist_NoMemoryWriteAfterCompute_*`).
///
/// **M.0 contract:** the no-op composer returns
/// <see cref="ConsciousSubstrateGist.Empty"/> regardless of input. The
/// telemetry harness still emits <c>M0_GIST_COMPOSITION</c> at the
/// consumer surface so the substrate-source-ratio baseline accumulates.
///
/// See `docs/spec/ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md`
/// for the full architectural framing.
/// </summary>
public interface IConsciousSubstrateGist
{
    /// <summary>
    /// Compose the gist for the given cycle's context snapshot. Returns
    /// <see cref="ConsciousSubstrateGist.Empty"/> when the feature flag
    /// is disabled or when no slice has content for this cycle.
    /// </summary>
    /// <param name="snapshot">
    /// The cycle's context snapshot. Composers MAY read
    /// <see cref="ContextSnapshot.RecentClosedConversation"/>,
    /// <see cref="ContextSnapshot.RecentMemory"/>, and
    /// <see cref="ContextSnapshot.RecentHistory"/> as input. Composers
    /// MUST NOT mutate the snapshot.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConsciousSubstrateGist> ComputeGistAsync(
        ContextSnapshot   snapshot,
        CancellationToken ct = default);
}
