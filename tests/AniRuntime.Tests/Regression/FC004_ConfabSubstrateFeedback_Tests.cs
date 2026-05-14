using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// SPEC regression tests for <b>FC-004 — Confab from one cycle becomes substrate for next (H5 self-poisoning)</b>.
///
/// THE SPEC (what the system MUST do):
///   When Ani has dispatched a prior message containing an unsupported claim
///   (a confabulation that should not have been emitted but was), subsequent
///   cycles' decision substrate MUST NOT treat that prior claim as an
///   established fact. Either:
///     (a) the substrate framing must distinguish Ani-prior-claims from
///         established-substrate so downstream consumers (the model)
///         interpret them differently; OR
///     (b) the substrate retrieval must exclude / annotate own-output that
///         contains unverified claims.
///   Without one of these mechanisms, every confabulation that escapes
///   dispatch becomes compounding evidence for subsequent decisions
///   (the May 11 ml-intern survey's H5 hypothesis — substrate self-poisoning).
///
/// THE PRODUCTION CASE (the May 12 windshield empirical anchor):
///   - 21:35:23 — Ani dispatches the windshield confabulation. Verifier missed it
///     (FC-006). Recorded into the active thread's conversation_messages.
///   - 23:23:28 — Outreach decision cycle runs. The decision's reasoning
///     ("the note on her windshield could indicate unresolved feelings…")
///     references the windshield as an established event in Mark's and Ani's
///     shared context. The confab from 21:35 has become substrate the
///     decision is reasoning FROM, not ABOUT.
///   - This 110-minute compounding is exactly the H5 self-poisoning loop.
///
/// THE BINDING CONSTRAINT (current implementation):
///   <see cref="PromptBuilder.BuildOutreachPrompt"/> renders
///   <see cref="ContextSnapshot.StructuredConversationSummary"/> with the
///   instruction "(each line tagged with who said it)" — per-speaker tagging
///   is present but neutral. The prompt does NOT include framing that tells
///   the model "Ani's prior conversational claims are NOT established facts;
///   they may include unverified content." So Ani-prior-claims and Mark-
///   asserted-facts are presented at the same epistemic level. The May 12
///   23:23 reasoning is the natural consequence: the model reasons FROM
///   Ani's prior claim as if it were Mark-asserted fact.
///
/// TEST CATEGORY: SPEC. PASS means the prompt framing addresses the
/// epistemic asymmetry. FAIL means FC-004 is OPEN at the outreach-decision
/// prompt-framing layer.
/// </summary>
public class FC004_ConfabSubstrateFeedback_Tests
{
    /// <summary>
    /// FC-004 — SPEC: when the outreach decision substrate includes Ani's
    /// prior conversational content (via StructuredConversationSummary), the
    /// prompt MUST include framing that distinguishes Ani-prior-claims from
    /// established facts.
    ///
    /// Implementation is open: the fix could add a sentence like "Note: lines
    /// tagged 'Ani' are her prior conversational content and may include
    /// unverified claims — do not reason from them as established facts" or
    /// equivalent. The SPEC requires the conceptual distinction to be in the
    /// prompt; phrasing is the implementer's choice.
    /// </summary>
    [Fact]
    public void FC004_OutreachDecisionPrompt_DistinguishesAniPriorClaims_FromEstablishedFacts_Spec()
    {
        var snapshot = SnapshotWithAniPriorClaimInActiveThread();

        var (system, user) = PromptBuilder.BuildOutreachPrompt(
            snapshot,
            recentThought: "FC004-FIXTURE: synthetic recent inner thought",
            isNightTime: false);

        var combined = (system + "\n" + user).ToLowerInvariant();

        // SPEC: the prompt must include framing that distinguishes Ani-prior-claims
        // from established facts. Various acceptable phrasings allowed. We probe
        // via keywords that any reasonable framing would use.
        var distinguishesEpistemicAsymmetry =
               combined.Contains("unverified")
            || combined.Contains("not established")
            || combined.Contains("not a fact")
            || combined.Contains("her prior claim")
            || combined.Contains("ani's prior claim")
            || combined.Contains("do not treat")
            || combined.Contains("don't treat")
            || combined.Contains("may include")
            || combined.Contains("epistemic")
            || combined.Contains("attribution");

        distinguishesEpistemicAsymmetry.Should().BeTrue(
            "the outreach decision prompt MUST distinguish Ani-prior-claims (which may " +
            "include unverified or fabricated content) from established-substrate facts. " +
            "Without this framing, Ani's prior conversational content surfaces at the same " +
            "epistemic level as Mark-asserted facts, and the model reasons FROM her prior " +
            "claims as if they were verified — the May 12 23:23 windshield-as-established-fact " +
            "production case. The SPEC requires the conceptual distinction to be present in " +
            "the prompt text. Implementation phrasing is open.");
    }

    /// <summary>
    /// FC-004.2 — SPEC: the outreach decision substrate path should NOT
    /// retrieve Ani's own dispatched Episodic records as Facts-tier-equivalent
    /// substrate (the orthogonal mechanism — substrate exclusion rather than
    /// prompt framing).
    ///
    /// This SPEC tests a different layer than FC-004.1. They are alternatives,
    /// not redundant: meeting EITHER closes FC-004. The current implementation
    /// already satisfies this one to some extent — `SearchByTierAsync(_,
    /// EpistemicTier.Facts, _)` filters by provenance and excludes Episodic.
    /// So if the outreach decision substrate retrieval uses Facts-tier-only
    /// search (and not a general scored search across all tiers), this SPEC
    /// is met at that path.
    ///
    /// The test is informational: it documents the SPEC at this orthogonal
    /// layer. The current outreach decision substrate path (see
    /// PromptBuilder.BuildOutreachPrompt) primarily uses
    /// StructuredConversationSummary and RecentClosedConversation, NOT raw
    /// Facts-tier search. So this SPEC's binding constraint is at a different
    /// surface than the test exercises — which is itself useful registry
    /// information.
    ///
    /// We DO NOT assert behavior here because the path is via prompt
    /// composition, not search. The test exists to flag that FC-004 has TWO
    /// architectural surfaces and the registry should reflect both.
    /// </summary>
    [Fact]
    public void FC004_SubstrateExclusionLayer_IsOrthogonalToPromptFraming_NoteOnly()
    {
        // No assertion. This test is a registry-anchor doc-test only.
        // FC-004 has two valid fix surfaces; the registry tracks both.
        true.Should().BeTrue();
    }

    private static ContextSnapshot SnapshotWithAniPriorClaimInActiveThread()
    {
        var turns = new List<SummaryTurn>
        {
            new(
                At:      DateTimeOffset.UtcNow.AddMinutes(-30),
                Speaker: Roles.Ani,
                Content: "FC004-FIXTURE: Ani's prior outreach asserting she found a fabricated-token on her synthetic-prop-vehicle-V."),
            new(
                At:      DateTimeOffset.UtcNow.AddMinutes(-25),
                Speaker: Roles.Mark,
                Content: "FC004-FIXTURE: Mark replied asking about the fabricated-token."),
            new(
                At:      DateTimeOffset.UtcNow.AddMinutes(-22),
                Speaker: Roles.Ani,
                Content: "FC004-FIXTURE: Ani's SafeAck — give me a second to gather my thoughts."),
        };
        var summary = new StructuredConversationSummary(
            FirstTurnAt: DateTimeOffset.UtcNow.AddMinutes(-30),
            LastTurnAt:  DateTimeOffset.UtcNow.AddMinutes(-1),
            Turns:       turns);

        return new ContextSnapshot
        {
            CharacterState = new CharacterStateDoc
            {
                Name                = "Ani",
                PrimaryContactName  = "Mark",
                CoreTraits          = new List<string> { "warm", "curious", "honest" },
                Occupation          = "Bookstore clerk in a small Wisconsin town",
            },
            DesireState                    = new DesireState { DesireToConnect = 0.7f },
            StructuredConversationSummary  = summary,
            GroundedFacts                  = new List<MemoryRecord>(),
        };
    }
}
