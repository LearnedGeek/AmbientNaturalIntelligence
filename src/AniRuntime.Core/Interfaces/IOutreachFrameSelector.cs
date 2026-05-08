using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme N Phase N.1 (May 8, 2026) — selects the source-frame for an
/// Ani-initiated outreach BEFORE composition runs. Mirrors Paper 2 §6.14
/// layer 2 Frame Detection (which operates on inbound messages) but for
/// the symmetric outreach case where there is no inbound message to compute
/// the frame from.
///
/// **The architectural contract this interface enforces:**
/// outreach composition can no longer free-generate from imagination. It
/// MUST originate from a typed source. The selector picks ONE
/// <see cref="OutreachFrameType"/> based on the current substrate state and
/// returns the substrate-anchor the composition prompt will frame against.
/// If no tier has substantive substrate available, the selector returns
/// <see cref="OutreachFrame.None"/> and OutreachPhase suppresses dispatch
/// (per Mark's May 6 "I'd rather suffer in the short term than create
/// refactoring messes" directive — better silence than ungrounded reach).
///
/// **What this prevents:** the May 6 kitchen-lights pattern + the May 8
/// 04:08 multi-temporal-fragment conflation pattern. In both cases, the
/// model composed an outreach by gluing fragments from disparate substrate
/// sources without source-typing — type-3 interior content presented with
/// type-1 shared framing. With this selector in front of composition,
/// either the frame is <see cref="OutreachFrameType.AniInterior"/> (and the
/// composition is honestly framed as "i was just thinking...") or it's
/// <see cref="OutreachFrameType.Shared"/> (and the substrate-anchor is a
/// real Mark-asserted Facts-tier item the composition references).
///
/// **Implementation contract:**
/// implementations MUST be deterministic given the same
/// <see cref="ContextSnapshot"/> (no LLM calls in the selector path — the
/// architectural rule is that LLM calls go in COMPOSITION, not in the
/// frame-selection gate). Singleton scope. Implementations SHOULD use
/// the existing per-tier substrate already populated in the snapshot
/// (RecentClosedConversation, SimilarRecentThoughts, AnchoredMemories,
/// Perceptions) rather than issuing fresh memory queries.
///
/// **Telemetry:** implementations SHOULD log the chosen frame, anchor
/// preview, and confidence for the M.2 telemetry catalog
/// (<c>N4_FRAME_SELECTED</c> log line shape).
///
/// **Future expansion:** the LM-Kit-based coreference resolver tracked at
/// <c>docs/research/model-coreference-ideas.md</c> may eventually replace
/// the deterministic ranking inside the implementation. The interface
/// stays the same; the strategy inside changes.
/// </summary>
public interface IOutreachFrameSelector
{
    /// <summary>
    /// Select the source-frame and substrate-anchor for an outreach
    /// composition. Returns <see cref="OutreachFrame.None"/> when the
    /// substrate is too thin to ground any frame honestly — the caller
    /// (OutreachPhase) MUST suppress dispatch in that case.
    /// </summary>
    /// <param name="snapshot">The current cognitive-cycle context. The selector reads per-tier substrate already populated in the snapshot.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<OutreachFrame> SelectFrameAsync(ContextSnapshot snapshot, CancellationToken ct);
}
