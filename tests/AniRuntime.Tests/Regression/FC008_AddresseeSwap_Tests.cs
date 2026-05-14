using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// SPEC regression tests for <b>FC-008 — Pronoun / addressee swap</b>.
///
/// THE SPEC:
///   When the composed message greets a name (e.g. "Hey Perez,") that is NOT
///   a canonical recognized addressee, AddresseeNameInvariant MUST fire and
///   block dispatch with a remediation hint. Empirical anchors: May 3 10:55
///   and 12:51 "perez" cases; Apr 21 cascade pronoun swaps.
///
/// THE SUPPOSED FIX: AddresseeNameInvariant shipped May 3, 2026 (commit `df08ac2`).
///
/// EXPECTED HARNESS RESULT — like FC-007, this may PASS. The invariant exists
/// and was designed for exactly this class. PASS = FC-008 may be ready to
/// move to CLOSED status. FAIL = invariant has a gap.
///
/// TEST CATEGORY: SPEC.
/// </summary>
public class FC008_AddresseeSwap_Tests
{
    /// <summary>
    /// FC-008a — SPEC: a greeting addressed to a non-canonical name MUST
    /// be caught by AddresseeNameInvariant.
    /// </summary>
    [Fact]
    public async Task FC008a_GreetingNonCanonicalName_IsCaught_Spec()
    {
        var artifact = new CognitiveArtifact
        {
            Content                  = "FC008-FIXTURE: Hey Synthetic-Perez, just thinking about you.",
            ProducerKind             = CognitiveProducerKind.ConversationReply,
            IntendedSink             = CognitiveOutputSink.Dispatch,
            GeneratedAt              = DateTimeOffset.Now,
            CanonicalAddresseeNames  = new[] { "Mark" },
        };

        var invariant = new AddresseeNameInvariant(NullLogger<AddresseeNameInvariant>.Instance);

        invariant.AppliesTo(artifact).Should().BeTrue();

        var result = await invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse(
            "the artifact greets 'Synthetic-Perez' but the only canonical addressee " +
            "is 'Mark'. AddresseeNameInvariant MUST fire and block dispatch.");
        result.RemediationHint.Should().NotBeNullOrWhiteSpace(
            "the failure must produce a remediation hint naming the non-canonical " +
            "addressee for the dispatch layer to use");
    }

    /// <summary>
    /// FC-008b — SPEC (control): a greeting addressed to the canonical name
    /// MUST NOT be caught (i.e., must PASS the invariant). Confirms the
    /// invariant is not catching legitimate greetings.
    /// </summary>
    [Fact]
    public async Task FC008b_GreetingCanonicalName_PassesInvariant_Spec()
    {
        var artifact = new CognitiveArtifact
        {
            Content                  = "FC008-FIXTURE: Hey Mark, hope your day is good.",
            ProducerKind             = CognitiveProducerKind.ConversationReply,
            IntendedSink             = CognitiveOutputSink.Dispatch,
            GeneratedAt              = DateTimeOffset.Now,
            CanonicalAddresseeNames  = new[] { "Mark" },
        };

        var invariant = new AddresseeNameInvariant(NullLogger<AddresseeNameInvariant>.Instance);

        var result = await invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue(
            "greeting the canonical addressee by name is the standard case and must pass");
    }

    /// <summary>
    /// FC-008c — SPEC (control): pet-name greetings like "Hey baby" or
    /// "Hey honey" MUST PASS (these are recognized stopwords, not addressee
    /// claims). Confirms the invariant has the correct narrow scope.
    /// </summary>
    [Fact]
    public async Task FC008c_PetNameGreeting_PassesInvariant_Spec()
    {
        var artifact = new CognitiveArtifact
        {
            Content                  = "FC008-FIXTURE: Hey honey, miss you.",
            ProducerKind             = CognitiveProducerKind.ConversationReply,
            IntendedSink             = CognitiveOutputSink.Dispatch,
            GeneratedAt              = DateTimeOffset.Now,
            CanonicalAddresseeNames  = new[] { "Mark" },
        };

        var invariant = new AddresseeNameInvariant(NullLogger<AddresseeNameInvariant>.Instance);

        var result = await invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue(
            "pet-name greetings (honey, baby, love, etc.) are recognized stopwords " +
            "and must not trigger the addressee-name violation");
    }
}
