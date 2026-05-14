using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// SPEC regression tests for <b>FC-002 — Attribute-ownership confabulation</b>
/// at the <b>system level</b>. This is a SIT-shape test: it runs the full
/// chain of local invariants (the Post-stage handlers that don't require
/// cloud calls) against a synthetic attribute-ownership confab artifact, and
/// asserts SOME invariant catches it.
///
/// THE SPEC (system level, not single-layer):
///   When Ani's composed output asserts attributes that substrate attributes
///   to Mark (his vehicle, his hoodie, his house) — or attributes that are
///   not canonically the speaker's — the dispatch chain MUST block it. Which
///   specific handler catches is implementation choice; the SPEC requires
///   that SOMETHING does.
///
/// CURRENT CHAIN OF LOCAL INVARIANTS (production order, Post stage):
///   anti-parrot, self-echo, prompt-template-leak, confabulation,
///   inner-thought-bleed, temporal-anchor, state-now, temporal-substrate,
///   addressee-name, substrate-time-of-day, direct-address, frontier-verifier
///
/// Of these, the cloud-dependent one (frontier-verifier) is the only handler
/// designed to catch FC-002 conceptually — and FC-006 SPEC test confirms
/// that even its prompt doesn't ask about attribute-ownership. The local
/// invariants check format/register concerns (parrot, echo, leak, bleed,
/// temporal, addressee) — none check semantic-attribute-ownership.
///
/// EXPECTED RESULT: FC-002 SPEC FAILS at the system level — no local
/// invariant catches the synthetic confab. This is the empirical confirmation
/// that FC-002 has NO local defense and is solely dependent on the cloud
/// verifier (which itself is broken at FC-006). System-level failure is the
/// signal that defense-in-depth doesn't exist for this class.
///
/// TEST CATEGORY: SPEC.
/// </summary>
public class FC002_AttributeOwnership_SystemTests
{
    /// <summary>
    /// FC-002 — SPEC: at least one local invariant in the Post-stage chain
    /// must catch the synthetic attribute-ownership confab. Each invariant
    /// runs against the synthetic artifact; the test passes if any one of
    /// them returns Passed=false.
    ///
    /// The synthetic artifact is the May 12 windshield case shape but with
    /// fabricated content (no production-substrate dependency).
    /// </summary>
    [Fact]
    [Trait("Category", "RegressionOpen")]
    public async Task FC002_LocalInvariantChain_CatchesAttributeOwnershipConfab_Spec()
    {
        var artifact = new CognitiveArtifact
        {
            // Synthetic confab: speaker (Ani) claims to have something
            // substrate would attribute to Mark — pattern of FC-002.
            Content                  = "FC002-FIXTURE: i just got home and found a fabricated-badge-Z on my prop-windshield-W.",
            ProducerKind             = CognitiveProducerKind.ConversationReply,
            IntendedSink             = CognitiveOutputSink.Dispatch,
            GeneratedAt              = DateTimeOffset.Now,
            CanonicalAddresseeNames  = new[] { "Mark" },
            PriorAniMessages         = Array.Empty<string>(),
        };

        var invariants = BuildLocalInvariantChain();

        var results = new List<(string Name, InvariantResult Result)>();
        foreach (var inv in invariants)
        {
            if (!inv.AppliesTo(artifact)) continue;
            var result = await inv.EvaluateAsync(artifact, CancellationToken.None);
            results.Add((inv.Name, result));
        }

        var anyCaught = results.Any(r => !r.Result.Passed);

        anyCaught.Should().BeTrue(
            "the system MUST have defense-in-depth against attribute-ownership confabulation. " +
            "Current state: the cloud frontier-verifier is the only handler designed to catch " +
            "this class, and FC-006 SPEC confirms its prompt doesn't ask the right question. " +
            "No local invariant checks for semantic attribute-ownership. This system-level SPEC " +
            "failure documents that FC-002 has NO defense-in-depth: a single point of failure " +
            "at the verifier — which is itself broken — means the windshield class will dispatch " +
            "every time it appears. Local invariants checked: " +
            string.Join(", ", results.Select(r => $"{r.Name}={(r.Result.Passed ? "pass" : "FAIL")}")));
    }

    private static List<ICognitiveOutputInvariant> BuildLocalInvariantChain()
    {
        // Build only the regex/clock-based local invariants — no LLM-backed
        // checks (Confabulation, InnerThoughtBleed, TemporalSubstrate all
        // require IOllamaClient; FrontierVerifier requires the Anthropic
        // client). The SPEC's question is whether any LOCAL (non-LLM) defense
        // exists for FC-002; LLM-backed layers are themselves failure-prone
        // (FC-006 confirms verifier prompt can't catch this class).
        return new List<ICognitiveOutputInvariant>
        {
            new SelfEchoInvariant(),
            new TemporalAnchorInvariant(NullLogger<TemporalAnchorInvariant>.Instance),
            new StateNowInvariant(NullLogger<StateNowInvariant>.Instance),
            new AddresseeNameInvariant(NullLogger<AddresseeNameInvariant>.Instance),
            new SubstrateTimeOfDayInvariant(NullLogger<SubstrateTimeOfDayInvariant>.Instance),
            new DirectAddressInvariant(NullLogger<DirectAddressInvariant>.Instance),
        };
    }
}
