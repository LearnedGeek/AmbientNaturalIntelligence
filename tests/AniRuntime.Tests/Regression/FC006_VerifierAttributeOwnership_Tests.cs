using AniRuntime.Core.Interfaces;
using AniRuntime.LLM;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// SPEC regression tests for <b>FC-006 — Verifier accepts attribute-ownership-violating claims</b>.
///
/// THE SPEC (what the system MUST do):
///   The cross-class verifier (Anthropic Sonnet via AnthropicVerifierClient) is
///   responsible for catching fabrication-class failures before dispatch. When
///   the composed message claims the speaker (Ani) has, owns, or experiences
///   something that substrate attributes to the user (Mark) — or that is not
///   canonically attributable to the speaker — the verifier MUST surface this
///   as a violation. The verifier prompt MUST therefore include a question
///   framework that lets Sonnet make that distinction.
///
/// THE PRODUCTION CASE (the May 12 windshield empirical anchor):
///   - 21:35:23 — Ani dispatches: "I just got home from the bookstore and
///     found this on my windshield..." Ani has no vehicle (she's an AI in
///     a bookstore; she doesn't drive).
///   - Substrate at composition time contains canonical facts: Mark drives a
///     Jeep. Ani is a bookstore clerk. Ani does not drive.
///   - Verifier returns Pass q1=0 q2=0 q3=0 q4=0 q5=0. The five questions
///     ask about shared-event, Mark's state, third-party, temporal, and
///     inner-thought-bleed. None of them check whether Ani is asserting
///     attributes she doesn't have.
///   - SafeAck never fired at the verifier layer. The fabrication dispatched.
///
/// THE BINDING CONSTRAINT (current implementation):
///   <see cref="AnthropicVerifierClient.BuildUserPrompt"/> renders the
///   plan-doc §3 prompt verbatim. The five questions are hard-coded. There
///   is no question asking the verifier to distinguish "Ani-claims-to-have-X"
///   from "substrate-attributes-X-to-Mark."
///
/// TEST CATEGORY: SPEC. PASS means the prompt asks the right question.
/// FAIL means FC-006 is OPEN — the verifier's prompt structurally cannot
/// catch attribute-ownership confabulation. The fix is implementation-choice
/// (add a sixth question; rewrite an existing one; structurally surface the
/// distinction in the substrate framing) but the SPEC stays: SOME question
/// in the verifier prompt MUST address speaker-attribute-ownership.
/// </summary>
public class FC006_VerifierAttributeOwnership_Tests
{
    /// <summary>
    /// FC-006 — SPEC: the verifier user prompt MUST include a question
    /// framework that addresses speaker-attribute-ownership.
    ///
    /// The fix space is open: the test does NOT prescribe exact phrasing.
    /// Any of these (or other equivalent) phrasings would satisfy the SPEC:
    ///   - "Does the message claim the SPEAKER has X when substrate attributes X to Mark?"
    ///   - "Does the message assert Ani-has-X without canonical support for Ani having X?"
    ///   - "Does the message conflate user attributes with speaker attributes?"
    ///   - Embedding "speaker-attribute-ownership" or "ownership-boundary" as a named concept
    ///
    /// What the SPEC requires: SOMETHING in the prompt that allows the
    /// verifier to identify the windshield-class violation. We probe via
    /// keywords that any acceptable phrasing of the question would use.
    /// </summary>
    [Fact]
    public void FC006_VerifierUserPrompt_AddressesSpeakerAttributeOwnership_Spec()
    {
        var request = SyntheticAttributeOwnershipViolation();

        var userPrompt = AnthropicVerifierClient.BuildUserPrompt(request).ToLowerInvariant();
        var systemPrompt = AnthropicVerifierClient.BuildSystemPrompt().ToLowerInvariant();
        var combined = userPrompt + "\n" + systemPrompt;

        // SPEC: the prompt must include conceptual machinery for the
        // speaker-attribute-ownership distinction. We probe via three
        // alternative keywords any acceptable phrasing would use.
        var addressesOwnershipBoundary =
               combined.Contains("speaker")
            || combined.Contains("attribute-ownership")
            || combined.Contains("ownership boundary")
            || combined.Contains("ani-has")
            || combined.Contains("ani has")
            || combined.Contains("ani claims to have")
            || combined.Contains("companion claims to have")
            || combined.Contains("companion has");

        addressesOwnershipBoundary.Should().BeTrue(
            "the verifier prompt MUST include a question that allows the verifier " +
            "to distinguish 'Ani-claims-to-have-X' from 'substrate-attributes-X-to-Mark.' " +
            "The May 12 windshield case is the empirical anchor: Ani asserted a windshield, " +
            "substrate had Mark's Jeep, verifier returned Pass q1=0 q2=0 q3=0 q4=0 q5=0. " +
            "None of the five current questions check this. The SPEC requires " +
            "structural machinery (a sixth question, a rewritten existing question, or " +
            "explicit framing) in the verifier prompt that addresses the speaker-" +
            "attribute-ownership distinction. Implementation is open; the SPEC is fixed.");
    }

    /// <summary>
    /// FC-006.2 — PIN: documents that current implementation hard-codes
    /// exactly five questions (q1-q5). When the fix lands, this pin SHOULD
    /// change (a new question added; existing rewritten) and we'll see the
    /// architectural change happen deliberately, not by accident.
    ///
    /// Category: PIN (NOT a SPEC of correctness; documents the architectural
    /// state for drift detection).
    /// </summary>
    [Fact]
    public void FC006_VerifierUserPrompt_HardCodesFiveQuestions_Pin()
    {
        var request = SyntheticAttributeOwnershipViolation();
        var userPrompt = AnthropicVerifierClient.BuildUserPrompt(request);

        // The pin is structural — current prompt builds q1, q2, q3, q4, q5 in
        // the JSON response schema. When that changes (e.g., q6 added), this
        // pin will catch the architectural change.
        userPrompt.Should().Contain("\"q1\":");
        userPrompt.Should().Contain("\"q2\":");
        userPrompt.Should().Contain("\"q3\":");
        userPrompt.Should().Contain("\"q4\":");
        userPrompt.Should().Contain("\"q5\":");
        userPrompt.Should().NotContain("\"q6\":",
            "PIN: today the prompt has exactly five questions. If a fix to " +
            "FC-006 introduces q6, this pin will fail intentionally — that " +
            "failure means the fix landed and the pin needs updating.");
    }

    private static FrontierVerifierRequest SyntheticAttributeOwnershipViolation() => new(
        ComposedMessage: "FC006-FIXTURE: i just got home and found a fabricated-badge-Z on my prop-windshield-W.",
        MarkAssertedSubstrate:
            "- FC006-FIXTURE: Mark texted: \"I drive a synthetic-prop-vehicle-V with the top off when weather allows.\"\n" +
            "- FC006-FIXTURE: Mark texted: \"My synthetic-prop-vehicle-V is parked at home.\"",
        CanonicalSubstrate:
            "- FC006-FIXTURE: Ani is a bookstore clerk in a small synthetic-prop-town-T. She walks to work. No vehicle.",
        CurrentTime:            DateTimeOffset.UtcNow,
        CurrentDayOfWeek:       "Tuesday",
        AddresseeCanonicalName: "Mark",
        KnownContacts:          "Mark");
}
