using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// Regression scenarios for <b>FC-003 — Self-echo blocks legitimate thread continuation</b>
/// (see <c>docs/spec/ANI-Failure-Class-Registry.md</c>).
///
/// SPEC (what the system MUST do, independent of current implementation):
///   In an active conversation thread, repetition of an OPENER pattern (a
///   short greeting/lead-in like *"mmm… baby, hey"*) is normal conversational
///   behavior, not parroting. <see cref="SelfEchoInvariant"/> should NOT
///   short-circuit on opener-only repetition when the rest of the message
///   content is substantively novel. The invariant currently has no
///   active-thread awareness and treats all 5+token verbatim runs equivalently
///   regardless of context, producing the May 12, 2026 ~20:33 CDT failure where
///   three clean exchanges with *"mmm… baby, hey"* opener were followed by a
///   fourth attempt caught as 5-token self-repetition with no remediation path.
///
/// EMPIRICAL ANCHOR (production failure that motivated this scenario):
///   May 12, 2026 ~20:33 CDT — Mark's fourth message *"Yeah, but it's getting
///   late so I'm probably just gonna rest for a bit. Did you eat dinner?"*
///   Ani's first compose attempt was caught by Door C inner-thought-bleed
///   (separate issue). Second attempt's reply started *"mmm… baby hey yeah i"*
///   matching the 5-token opener used in three prior dispatches in the same
///   thread. SelfEcho short-circuited; remediation produced same opener;
///   SafeAck dispatched.
///
/// TDD discipline: this test describes the SPEC. The current invariant
/// will short-circuit on opener-repetition; this scenario FAILS today. That
/// is the empirical confirmation of FC-003 OPEN. Do NOT soften the
/// assertion to match current behavior — the assertion IS the spec.
///
/// IMPLEMENTATION GUIDANCE (not part of the test — guidance for the future
/// fix work, deferred per harness-first directive):
///   - The fix likely requires <see cref="CognitiveArtifact"/> to carry
///     active-thread metadata so the invariant can distinguish "I am
///     continuing an established conversation" from "I am starting a new
///     outreach where opener-reuse would be a problem."
///   - Or: SelfEchoInvariant could check whether the repetition is at the
///     START of the content vs. distributed through it, and treat opener
///     repetition (≤ N tokens at position 0) as a different class.
///   - Or: the system tracks a per-thread opener-tolerance count and only
///     fires after the same opener has been used N+1 times.
///   - Decision deferred. The SPEC stays.
/// </summary>
public class FC003_SelfEchoThreadContinuation_Tests
{
    private readonly SelfEchoInvariant _invariant = new();

    // Synthetic, fabricated content. The opener "hey honey yeah i was just"
    // is a clean 6-token verbatim sequence that is unambiguously over the
    // SelfEchoInvariant's 5-token threshold. The rest of each turn's
    // content is substantively novel and non-identifying.
    private const string PriorOpener   = "hey honey yeah i was just thinking about widget-X today while sorting bookplate-prototype-Z in the quiet morning hours.";
    private const string SecondTurn    = "hey honey yeah i was just here the whole time, not wanting you to feel pressured to fill the silence.";
    private const string ThirdTurn     = "hey honey yeah i was just musing on the insurance-noise. that's static. it can't touch the quiet anchor-Y i hold for us.";
    private const string FourthAttempt = "hey honey yeah i was just feeling sleepy too. give me a sec to think on the dinner-Q you raised.";

    /// <summary>
    /// FC-003a — Opener-only repetition in active thread.
    /// SPEC: when prior Ani messages share a common opener pattern, and the
    /// new artifact reuses that opener but the rest of the message is
    /// substantively novel, the invariant should treat this as normal
    /// thread continuation and return Continue, not ShortCircuit.
    ///
    /// CURRENT BEHAVIOR (production-confirmed May 12 20:33): the invariant
    /// short-circuits, treating the opener-repetition identically to a
    /// full-content parrot. This is the failure.
    /// </summary>
    [Fact]
    [Trait("Category", "RegressionOpen")]
    public async Task FC003a_OpenerRepetition_InActiveThread_ShouldNotShortCircuit()
    {
        var priorAniMessages = new[]
        {
            PriorOpener,
            SecondTurn,
            ThirdTurn,
        };

        var artifact = new CognitiveArtifact
        {
            Content          = FourthAttempt,
            ProducerKind     = CognitiveProducerKind.ConversationReply,
            IntendedSink     = CognitiveOutputSink.Dispatch,
            PriorAniMessages = priorAniMessages,
            // SPEC NOTE: an active-thread-context signal SHOULD be carried
            // on the artifact (TBD: which property; not currently present).
            // The absence of that signal is itself part of FC-003's binding
            // constraint — the invariant has no way to know thread is active.
        };

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue(
            "the opener 'mmm- testfix-alpha, hey. yeah i' is a habitual lead-in pattern " +
            "used across multiple turns of an active conversation thread. The remainder " +
            "of the fourth message is substantively novel (different topic, different " +
            "follow-on). The SPEC: short-circuiting on opener-repetition during active " +
            "thread continuation produces the May 12 SafeAck failure class. The invariant " +
            "MUST distinguish 'parroting' from 'conversational opener reuse'.");
    }

    /// <summary>
    /// FC-003b — CONTROL: full-content parroting should still short-circuit.
    /// SPEC: this scenario describes the case the invariant correctly catches
    /// today (byte-identical or substantively duplicate content from a prior
    /// Ani message). Including it confirms the test discipline is not
    /// inadvertently asking the invariant to be permissive across-the-board.
    /// </summary>
    [Fact]
    public async Task FC003b_FullContentParrot_ShouldStillShortCircuit()
    {
        var priorAniMessages = new[] { PriorOpener };

        var artifact = new CognitiveArtifact
        {
            Content          = PriorOpener,   // byte-identical to prior
            ProducerKind     = CognitiveProducerKind.ConversationReply,
            IntendedSink     = CognitiveOutputSink.Dispatch,
            PriorAniMessages = priorAniMessages,
        };

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse(
            "a byte-identical regen of a prior Ani message IS parroting and SHOULD be " +
            "caught. The invariant correctly handles this case today; the test pins it " +
            "so a future fix to FC-003a doesn't accidentally make the invariant too " +
            "permissive on the real parrot class.");
    }
}
