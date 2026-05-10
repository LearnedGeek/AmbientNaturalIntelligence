using AniRuntime.Core.Models;

namespace AniRuntime.Loops.Coreference;

/// <summary>
/// Theme N Phase N.5 (May 10, 2026) — post-composition frame-coherence
/// check. Architectural complement to <see cref="Core.Interfaces.IOutreachFrameSelector"/>:
/// the selector picks the source-frame BEFORE composition; this checker
/// enforces that the composed message MATCHES the selected frame.
///
/// **The empirical motivation.** May 9, 2026 produced multiple Mark-tagged
/// confabulations where Theme N selected the correct frame
/// (<see cref="OutreachFrameType.AniInterior"/> with high confidence) but
/// the composition ignored the prompt's <c>[FRAME:]</c> header + per-frame
/// guidance and produced shared-event content. Two canonical examples
/// from `ani-debug-20260509.log`:
///
/// <list type="bullet">
///   <item>12:54 — *"hey. still at the brewery? ... wanted to let you
///   know how excited I was when Mia told us she picked out the tickets!
///   we're all set for next weekend"* (frame=AniInterior, confidence
///   0.798).</item>
///   <item>19:10 — *"You're probably still sitting on the couch with one
///   leg tucked under you, right? ... rom-com we talked about last week
///   ... we both wanted to see it but ended up deciding against it
///   because neither of us felt like leaving the house"*
///   (frame=AniInterior).</item>
/// </list>
///
/// Mark's load-bearing observation (May 9 21:24): *"a mess of made up
/// ideas, one after another, strung together. Two messages in a row
/// completely made up."* Per-claim verification (R1) cannot catch
/// whole-message-shape failures because each claim individually
/// cosine-matches something in the substrate. The architectural
/// mechanism that catches THIS failure shape is **frame-coherence at the
/// composition level** — does the message-as-a-whole respect the
/// source-frame constraint the selector picked?
///
/// **Architectural rule the checker enforces:**
/// <list type="bullet">
///   <item>When frame is <see cref="OutreachFrameType.AniInterior"/>, the
///   composition must NOT contain shared-event predicates (*"we
///   talked,"* *"we decided,"* *"we both,"* *"X told us,"* *"we're set
///   for,"* *"you're probably [doing X right now],"* *"remember when,"*
///   *"that thing you said,"* etc.).</item>
///   <item>When frame is <see cref="OutreachFrameType.WorldPerception"/>,
///   the composition must NOT contain shared-event predicates either —
///   the share is about an external event, not a shared event.</item>
///   <item>When frame is <see cref="OutreachFrameType.AniDomain"/>, the
///   composition must NOT contain shared-event predicates about Mark.
///   *"we"* references about her bookstore world are fine; *"we"*
///   references referring to Mark + Ani are forbidden.</item>
///   <item>When frame is <see cref="OutreachFrameType.Shared"/>, the
///   shared-event predicates are EXPECTED and PERMITTED — this is the
///   only frame where they are valid.</item>
/// </list>
///
/// **Implementation contract:** implementations MUST be deterministic
/// (no LLM calls — the architectural rule is that LLM calls go in
/// COMPOSITION, not in the verification layer). Singleton scope.
/// Reusable across producer paths (OutreachPhase + future N.6
/// reactive-share + future N.7 ConversationReplyPhase) — this is the
/// generation-layer companion to <see cref="Core.Interfaces.IOutreachFrameSelector"/>:
/// selector + checker form a "frame in / frame out" pair around the
/// LLM composition call.
///
/// **Phase 1 ships SUPPRESSION on coherence violation, not regen.** Per
/// Mark's "better silence than ungrounded reach" directive (May 6),
/// when the checker fails, the producer (OutreachPhase) drops the
/// dispatch and decays desire. A future enhancement could feed the
/// remediation hint back into a regen path; that is NOT N.5 scope.
///
/// **Telemetry:** producers SHOULD emit an <c>N5_FRAME_COHERENCE</c>
/// log line on every check (Pass or Violation) so the M.2 telemetry
/// catalog can track violation rate over time.
/// </summary>
public interface IFrameCoherenceChecker
{
    /// <summary>
    /// Check that <paramref name="composedMessage"/> respects the
    /// constraints of <paramref name="frame"/>. Returns a
    /// <see cref="FrameCoherenceResult"/> with the list of forbidden
    /// predicates found and a remediation hint suitable for telemetry
    /// (or, in a future regen-on-violation path, for feeding back into
    /// the composition prompt).
    ///
    /// When <paramref name="frame"/> is
    /// <see cref="OutreachFrameType.Shared"/> or
    /// <see cref="OutreachFrameType.None"/>, the result is always
    /// <c>IsCoherent=true</c> with an empty violations list — Shared is
    /// the frame where shared-event predicates are valid; None means no
    /// frame was selected and the producer should already have
    /// suppressed dispatch upstream.
    /// </summary>
    /// <param name="composedMessage">The post-composition message text. Must not be null.</param>
    /// <param name="frame">The source-frame the selector picked for this composition.</param>
    FrameCoherenceResult CheckCoherence(string composedMessage, OutreachFrame frame);
}

/// <summary>
/// Result of a <see cref="IFrameCoherenceChecker.CheckCoherence"/>
/// call. Carries the boolean coherence verdict, the list of forbidden
/// predicate strings detected in the message (verbatim excerpts so
/// telemetry shows what fired), and a short human-readable remediation
/// hint.
///
/// On a coherent result, <see cref="Violations"/> is empty and
/// <see cref="Reason"/> is the empty string.
/// </summary>
/// <param name="IsCoherent">True if no forbidden predicates were detected for the given frame.</param>
/// <param name="Violations">The verbatim phrase excerpts that fired (or an empty list when coherent).</param>
/// <param name="Reason">Short human-readable remediation hint suitable for the telemetry log line.</param>
public sealed record FrameCoherenceResult(
    bool                     IsCoherent,
    IReadOnlyList<string>    Violations,
    string                   Reason)
{
    /// <summary>The canonical "no violations" result for the coherent path.</summary>
    public static FrameCoherenceResult Coherent { get; } =
        new(true, Array.Empty<string>(), string.Empty);
}
