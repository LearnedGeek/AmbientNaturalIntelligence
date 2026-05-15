namespace AniRuntime.Core.Models;

/// <summary>
/// FC-010 architectural primitive (2026-05-14) — the engagement mode the
/// reply path uses when responding to a follow-up about prior dispatched
/// content. Producer-side selection at <c>ConversationReplyPhase</c>;
/// post-stage invariants (e.g. <c>SelfEchoInvariant</c>) consult the
/// mode to decide whether to gate strictly or relax for legitimate
/// thread continuation.
///
/// **Why this exists.** The May 12 21:35–21:51 windshield case
/// established the gating-asymmetry failure: front door (outreach gates)
/// passed a novel self-world claim; back door (reply gates) had no
/// architectural primitive for engaging with that prior dispatch, so the
/// model parroted → self-echo caught → SafeAck. The continuation modes
/// below name the three legitimate engagement paths the reply path was
/// previously missing.
///
/// **Status.** M.0-equivalent — enum + property exist as the
/// architectural primitive. Runtime selection logic (producer chooses
/// mode based on inbound + prior dispatch) is the next FC-010 phase;
/// the SPEC at this layer only asserts the primitive exists.
/// </summary>
public enum ReplyContinuationMode
{
    /// <summary>
    /// Not a thread-continuation reply (default). Standard reply path
    /// invariants apply with full strictness.
    /// </summary>
    None = 0,

    /// <summary>
    /// Natural expansion — the prior content was novel self-world
    /// material; the reply engages with it the way a friend would
    /// engage with new information ("oh you got a car?" → "yeah, last
    /// week"). Self-echo relaxes its check on references to the prior
    /// dispatch when this mode is set.
    /// </summary>
    Expansion = 1,

    /// <summary>
    /// Substrate-supported callback — the prior content references a
    /// real prior conversation; the reply pulls the relevant closed-
    /// conversation gist as substrate. Depends on FC-011 (Vibe Loop
    /// V1.5) retrieval surface; consumer wiring waits until V1.5 ships.
    /// </summary>
    SubstrateSupportedCallback = 2,

    /// <summary>
    /// Honest walkback — the prior content was speculative, imagined,
    /// or her own modal phrasing; the reply acknowledges this rather
    /// than doubling down ("honestly i was just imagining"). Producer
    /// selects this mode when prior content's modality was modal or
    /// when retrieval finds no substrate to back the claim.
    /// </summary>
    Walkback = 3,
}
