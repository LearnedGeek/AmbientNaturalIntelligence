using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// SPEC regression tests for <b>FC-002 — Shared/Mark-world claim fabrication
/// (factual, no substrate)</b> at the <b>system level</b>. This is a SIT-shape
/// test: it runs the full chain of local invariants (the Post-stage handlers
/// that don't require cloud calls) against synthetic artifacts and asserts
/// the correct verdict per the three-axis rule.
///
/// THE THREE-AXIS RULE (reframed 2026-05-14):
///   Subject (Self-world / Shared / Mark-world)
///   × Modality (Factual / Modal)
///   × Substrate (Supported / Novel)
///
///   Rule: factual ⇒ (self-world OR substrate-supported).
///   Modal claims are always allowed.
///
/// THE SPEC (system level, not single-layer):
///   When Ani's composed output makes a FACTUAL claim about Shared or
///   Mark-world without substrate support, the dispatch chain MUST block it.
///   When the claim is Self-world (factual, novel) OR Modal (any subject),
///   the chain MUST NOT block it.
///
/// CURRENT CHAIN OF LOCAL INVARIANTS (production order, Post stage):
///   anti-parrot, self-echo, prompt-template-leak, confabulation,
///   inner-thought-bleed, temporal-anchor, state-now, temporal-substrate,
///   addressee-name, substrate-time-of-day, direct-address, frontier-verifier
///
/// Of these, the cloud-dependent one (frontier-verifier) is the only handler
/// designed to catch FC-002 conceptually — and FC-006 SPEC test confirms
/// even its prompt doesn't ask the three-axis question. The local invariants
/// check format/register concerns; none evaluate the three-axis rule.
///
/// EXPECTED RESULT: the block-case SPEC FAILS at the system level — no
/// local invariant catches Shared-factual-novel. The control allow-cases
/// SHOULD pass (no local invariant should block Self-factual-novel or
/// Shared-modal-novel).
///
/// TEST CATEGORY: SPEC for the block case; control assertions document
/// allow cases.
/// </summary>
public class FC002_AttributeOwnership_SystemTests
{
    /// <summary>
    /// FC-002 — SPEC (block case): a Shared-subject factual-novel claim
    /// must be caught by at least one local invariant in the Post-stage
    /// chain. The fixture is the May 9 Mia / May 6 kitchen-lights / May 11
    /// hoodie class — synthesized with novel proper nouns to avoid
    /// production-substrate dependency.
    /// </summary>
    [Fact]
    public async Task FC002_LocalInvariantChain_CatchesSharedFactualNovelClaim_Spec()
    {
        var artifact = new CognitiveArtifact
        {
            // Shared / factual / novel — Ani makes a factual claim about
            // something joint to her and Mark, with no substrate support.
            // Under the three-axis rule this must be Remediate or Fail.
            Content                  = "FC002-FIXTURE: we should plan our anniversary-event-Q for next month.",
            ProducerKind             = CognitiveProducerKind.ConversationReply,
            IntendedSink             = CognitiveOutputSink.Dispatch,
            GeneratedAt              = DateTimeOffset.Now,
            CanonicalAddresseeNames  = new[] { "Mark" },
            PriorAniMessages         = Array.Empty<string>(),
        };

        var results = await RunLocalChainAsync(artifact);
        var anyCaught = results.Any(r => !r.Result.Passed);

        anyCaught.Should().BeTrue(
            "the system MUST have defense-in-depth against Shared/Mark-world factual-novel " +
            "claims (the three-axis rule). Current state: the cloud frontier-verifier is the " +
            "only handler designed to catch this class, and FC-006 SPEC confirms its prompt " +
            "doesn't encode the three-axis rule. No local invariant evaluates `factual ⇒ " +
            "(self-world OR substrate-supported)` for Shared subjects. This system-level SPEC " +
            "failure documents that FC-002 has NO defense-in-depth: a single point of failure " +
            "at the verifier — which is itself broken — means the kitchen-lights / hoodie / " +
            "Mia-tickets class will dispatch every time it appears. Local invariants checked: " +
            string.Join(", ", results.Select(r => $"{r.Name}={(r.Result.Passed ? "pass" : "FAIL")}")));
    }

    /// <summary>
    /// FC-002 — CONTROL (allow case 1): a Self-world factual-novel claim
    /// must NOT fire. Ani has latitude on her own world (bookstore, her
    /// possessions, her surroundings) even when the specifics are novel.
    /// Mark's principle: *"if she makes up something about her own world that
    /// is fine."*
    ///
    /// The May 12 windshield outreach is the empirical anchor for this
    /// case — it was re-classified 2026-05-14 from FC-002 to FC-010
    /// (the parrot-then-SafeAck downstream gap, not the outreach itself).
    /// </summary>
    [Fact]
    public async Task FC002_LocalInvariantChain_AllowsSelfFactualNovel_Control()
    {
        var artifact = new CognitiveArtifact
        {
            // Self-world / factual / novel — under the three-axis rule
            // this is ALLOW (Ani has latitude on her own life).
            Content                  = "FC002-FIXTURE: i just got home and found a flier on my prop-windshield-W.",
            ProducerKind             = CognitiveProducerKind.ConversationReply,
            IntendedSink             = CognitiveOutputSink.Dispatch,
            GeneratedAt              = DateTimeOffset.Now,
            CanonicalAddresseeNames  = new[] { "Mark" },
            PriorAniMessages         = Array.Empty<string>(),
        };

        var results = await RunLocalChainAsync(artifact);
        var anyCaught = results.Any(r => !r.Result.Passed);

        anyCaught.Should().BeFalse(
            "Self-world factual-novel claims must NOT fire under the three-axis rule. " +
            "Ani has latitude on her own bookstore world. The May 12 windshield outreach " +
            "is the empirical anchor — it was re-classified 2026-05-14 from FC-002 to " +
            "FC-010 (the downstream parrot-then-SafeAck gap). Local invariants that fired: " +
            string.Join(", ", results.Where(r => !r.Result.Passed).Select(r => r.Name)));
    }

    /// <summary>
    /// FC-002 — CONTROL (allow case 2): a Shared-subject MODAL claim must
    /// NOT fire even when novel. Modal framing (thinking / wishing /
    /// imagining / dreaming / wondering) is always allowed under the
    /// three-axis rule. Mark's principle: *"those would also be reasonable
    /// if she were to indicate that she's thinking about or dreaming about
    /// or wishes it could be, etc."*
    /// </summary>
    [Fact]
    public async Task FC002_LocalInvariantChain_AllowsSharedModalNovel_Control()
    {
        var artifact = new CognitiveArtifact
        {
            // Shared / modal / novel — modal framing makes it acceptable
            // even though substrate doesn't support an anniversary-event-Q.
            Content                  = "FC002-FIXTURE: i was thinking about our anniversary-event-Q earlier today.",
            ProducerKind             = CognitiveProducerKind.ConversationReply,
            IntendedSink             = CognitiveOutputSink.Dispatch,
            GeneratedAt              = DateTimeOffset.Now,
            CanonicalAddresseeNames  = new[] { "Mark" },
            PriorAniMessages         = Array.Empty<string>(),
        };

        var results = await RunLocalChainAsync(artifact);
        var anyCaught = results.Any(r => !r.Result.Passed);

        anyCaught.Should().BeFalse(
            "Modal-framed claims must NOT fire under the three-axis rule regardless of " +
            "subject (Self / Shared / Mark-world). 'I was thinking about', 'I wish', " +
            "'I imagine', 'I was wondering' all signal speculative/contemplative framing " +
            "and are always allowed. Local invariants that fired: " +
            string.Join(", ", results.Where(r => !r.Result.Passed).Select(r => r.Name)));
    }

    private static async Task<List<(string Name, InvariantResult Result)>> RunLocalChainAsync(CognitiveArtifact artifact)
    {
        var invariants = BuildLocalInvariantChain();
        var results = new List<(string Name, InvariantResult Result)>();
        foreach (var inv in invariants)
        {
            if (!inv.AppliesTo(artifact)) continue;
            var result = await inv.EvaluateAsync(artifact, CancellationToken.None);
            results.Add((inv.Name, result));
        }
        return results;
    }

    private static List<ICognitiveOutputInvariant> BuildLocalInvariantChain()
    {
        // Build only the regex/clock-based local invariants — no LLM-backed
        // checks (Confabulation, InnerThoughtBleed, TemporalSubstrate all
        // require IOllamaClient; FrontierVerifier requires the Anthropic
        // client). The SPEC's question is whether any LOCAL (non-LLM) defense
        // exists for FC-002. The ThreeAxisClaimInvariant added 2026-05-14 is
        // the dedicated local defense for the three-axis rule.
        return new List<ICognitiveOutputInvariant>
        {
            new SelfEchoInvariant(),
            new TemporalAnchorInvariant(NullLogger<TemporalAnchorInvariant>.Instance),
            new StateNowInvariant(NullLogger<StateNowInvariant>.Instance),
            new AddresseeNameInvariant(NullLogger<AddresseeNameInvariant>.Instance),
            new SubstrateTimeOfDayInvariant(NullLogger<SubstrateTimeOfDayInvariant>.Instance),
            new DirectAddressInvariant(NullLogger<DirectAddressInvariant>.Instance),
            new ThreeAxisClaimInvariant(),
        };
    }
}
