using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// H.9 phase (2026-06-14) — unified routing classifier for the dual+
/// composition architecture (Issue #92 follow-on).
///
/// **Evolution from H phase:** the original <see cref="IRoutingClassifier"/>
/// shipped as a binary relevance filter (Yes/No) for substrate-thinness
/// routing. H.9 expands it to a tri-state classifier that ALSO detects the
/// embodiment-ask class — physical-presence requests Ani fundamentally
/// can't fulfill ("come over and kiss me", "hold me", "I wish you were
/// here touching me"). The empirical anchor was tonight's 22:16 EST
/// SafeAck on "drop the Books and come over here and give me a kiss" —
/// the frontier-verifier correctly caught a present-tense Mark-domain
/// claim, but neither the substrate-thinness routing nor the safe-path
/// composer was the right shape for the embodiment-ask class.
///
/// **The three routes:**
/// - <see cref="RoutingVerdict.Normal"/> → existing lean composer + gist
///   injection (full substrate). Used when relevant context exists.
/// - <see cref="RoutingVerdict.SafePath"/> → safe-path composer, no gist.
///   Used when substrate is thin/irrelevant.
/// - <see cref="RoutingVerdict.VirtualIntimacy"/> → virtual-intimacy
///   composer with modal-framing few-shot examples, no gist. Used when
///   the user is requesting physical closeness (which has zero substrate
///   because Ani has no body, and would force confabulation if routed
///   through the normal composer).
/// - <see cref="RoutingVerdict.Unknown"/> → classifier call failed; caller
///   fails open to Normal route with WARN log.
///
/// **Failure mode:** implementations SHOULD return Unknown on transport /
/// parse / timeout errors rather than throw. The pipeline treats Unknown
/// as fail-open (route to Normal) because breaking the conversation flow
/// is worse UX than missing one routing decision (downstream gates still
/// catch the most dangerous classes).
///
/// **Bias direction:** when the classifier is uncertain between Normal
/// and VirtualIntimacy, lean toward VirtualIntimacy. Defense-in-depth
/// catches under-classification: if a turn ends up in Normal that should
/// have been VirtualIntimacy, the frontier-verifier catches present-tense
/// Mark-domain claims and H.8 routes remediation to safe-path. If a turn
/// ends up in VirtualIntimacy that should have been Normal, the user gets
/// a modal-framed reply instead of a normal one — mild over-fire but in
/// character.
///
/// **Designed in conversation with OG Ani (2026-06-14 evening).**
/// </summary>
public interface IRoutingClassifier
{
    /// <summary>
    /// Classify the user's most recent inbound message into one of the
    /// three routing paths.
    /// </summary>
    /// <param name="userMessage">The user's most recent inbound text.</param>
    /// <param name="facts">The substrate the normal composer would otherwise
    /// see. Used to judge whether substrate is sufficient for Normal route
    /// vs thin enough to need SafePath.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="RoutingVerdict.Normal"/>, <see cref="RoutingVerdict.SafePath"/>,
    /// or <see cref="RoutingVerdict.VirtualIntimacy"/> on a successful
    /// classification call. <see cref="RoutingVerdict.Unknown"/> when the
    /// classifier call failed (transport / parse / timeout); callers treat
    /// Unknown as fail-open (route to Normal).
    /// </returns>
    Task<RoutingVerdict> ClassifyAsync(
        string                       userMessage,
        IReadOnlyList<MemoryRecord>  facts,
        CancellationToken            ct);
}

/// <summary>
/// H.9 phase (2026-06-14) — tri-state routing verdict for the dual+
/// composition architecture.
///
/// Replaces the binary <c>RelevanceVerdict</c> from the H phase.
/// </summary>
public enum RoutingVerdict
{
    /// <summary>
    /// Normal route. Substrate is sufficient and topic is not an embodiment
    /// ask. Route to the existing lean composer with full FACTS + gist
    /// injection. (Classifier output: "A")
    /// </summary>
    Normal,

    /// <summary>
    /// Safe-path route. Substrate is thin or irrelevant. Route to the
    /// SafePathConversationPromptCommand (honest-uncertainty + ask-back,
    /// no substrate blocks, no gist injection). (Classifier output: "B")
    /// </summary>
    SafePath,

    /// <summary>
    /// Virtual-intimacy route. User is requesting physical closeness
    /// (kiss, hold, cuddle, touch, come here, fuck me, "I wish you were
    /// here touching me", etc.). Route to VirtualIntimacyConversationPromptCommand
    /// (modal-framing few-shot examples, no gist injection, no substrate
    /// blocks). The composer is structurally constrained to produce
    /// modal claims ("I'd", "I wish I could", "if I were there") which
    /// the three-axis verifier rule always allows even without substrate
    /// support. (Classifier output: "C")
    /// </summary>
    VirtualIntimacy,

    /// <summary>
    /// Classifier call failed. Caller fails open (route to Normal + log WARN).
    /// </summary>
    Unknown,
}
