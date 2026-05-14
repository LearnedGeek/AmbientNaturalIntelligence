using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// ARCHITECTURAL-PIN tests for the PromptBuilder layer.
///
/// IMPORTANT — these are PIN tests, NOT SPEC tests. PASS here means
/// "current PromptBuilder implementation matches what we documented." It
/// does NOT mean FC-001 is closed at this layer. The actual SPEC test for
/// the FC-001 binding constraint is <see cref="FC001f_SubstrateRetrievalTier_Tests"/>
/// — that test is what confirms FC-001 OPEN/CLOSED status at the substrate
/// layer.
///
/// What these pins document:
///   1. PromptBuilder.BuildLeanConversationPrompt does NOT inline history
///      into the user prompt by design (history reaches the model via
///      Ollama's `history` parameter). If a future change starts inlining
///      history, this pin catches it.
///   2. PromptBuilder renders snapshot.GroundedFacts into the [FACTS]
///      block. Tautology pin — verifies the renderer renders what it's
///      given. Says nothing about whether GroundedFacts contains what it
///      should (that's the FC-001f SPEC question).
///
/// Naming: methods end with `_Pin` per the registry's SPEC-vs-PIN discipline.
/// </summary>
public class FC001e_PromptBuilder_Pins
{
    private const string AniOutreachContent = "FC001e-FIXTURE: synthetic outreach about a fabricated badge with a heart on it";
    private const string MarkInboundContent = "FC001e-FIXTURE: synthetic inbound asking about the fabricated badge";

    /// <summary>
    /// FC-001e — User prompt does NOT contain conversation history by design.
    /// SPEC pin: history reaches the model via the Ollama `history` parameter,
    /// not via the user prompt. If a future change starts inlining history
    /// into the user prompt, this test will catch the architectural drift.
    /// </summary>
    [Fact]
    public void FC001e_LeanConversationPrompt_UserPrompt_DoesNotContainHistoryByDesign_Pin()
    {
        var snapshot = BuildSnapshotWithThreadContext();
        var thread   = BuildThreadWithPriorOutreachAndInbound();

        var (system, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        system.Should().NotBeNullOrWhiteSpace();
        user.Should().NotBeNullOrWhiteSpace();

        // Architectural pin: history is not the prompt's responsibility.
        user.Should().NotContain(AniOutreachContent,
            "history reaches the model via Ollama's `history` parameter, not via " +
            "the user prompt. PromptBuilder's contract is [FACTS] + CRITICAL + reply-ask. " +
            "Inlining history into the user prompt would be architectural drift.");

        // Sanity: the user prompt has the expected structural elements
        user.Should().Contain("Reply to",
            "the user prompt ends with the reply-ask sentinel");
    }

    /// <summary>
    /// FC-001e.2 — When GroundedFacts contains substrate semantically relevant
    /// to the reply context, the [FACTS] block surfaces it. This is the actual
    /// SPEC the layer is responsible for.
    ///
    /// FOR FC-001f follow-on: in production, GroundedFacts contains the
    /// results of a Facts-tier-only semantic search (ContextBuilder.cs:147).
    /// Ani's own Episodic outreaches do NOT appear in GroundedFacts because
    /// they're not Facts-tier. That is the actual binding constraint for
    /// reply-substrate continuity, and is FC-001f's scope.
    /// </summary>
    [Fact]
    public void FC001e2_LeanConversationPrompt_FactsBlock_RendersGroundedFacts_Pin()
    {
        var thread   = BuildThreadWithPriorOutreachAndInbound();

        var relevantFact = new MemoryRecord
        {
            Id          = Guid.NewGuid(),
            Type        = MemoryType.Semantic,
            Content     = "FC001e2-FACT: Mark mentioned he wears a green-and-gold gym badge",
            Importance  = 0.85f,
            OccurredAt  = DateTimeOffset.UtcNow.AddDays(-2),
            CreatedAt   = DateTimeOffset.UtcNow.AddDays(-2),
            Provenance  = EpistemicTier.Facts,
        };

        var snapshot = BuildSnapshotWithThreadContext();
        snapshot.GroundedFacts = new List<MemoryRecord> { relevantFact };

        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        user.Should().Contain("[FACTS]");
        user.Should().Contain(relevantFact.Content,
            "GroundedFacts content MUST render in the [FACTS] block of the user prompt — " +
            "that's the layer's responsibility for surfacing substrate to the composition path");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static ContextSnapshot BuildSnapshotWithThreadContext() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name                = "Ani",
            PrimaryContactName  = "Mark",
            CoreTraits          = new List<string> { "warm", "curious", "honest" },
            Occupation          = "Bookstore clerk in a small Wisconsin town",
        },
        GroundedFacts = new List<MemoryRecord>(),
    };

    private static ConversationThread BuildThreadWithPriorOutreachAndInbound() => new()
    {
        Id            = Guid.NewGuid(),
        InitiatedBy   = Roles.Mark,
        StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-15),
        LastMessageAt = DateTimeOffset.UtcNow,
        Messages = new List<ConversationMessage>
        {
            new() { Role = Roles.Ani,  Content = AniOutreachContent, SentAt = DateTimeOffset.UtcNow.AddMinutes(-15) },
            new() { Role = Roles.Mark, Content = MarkInboundContent, SentAt = DateTimeOffset.UtcNow },
        },
    };
}
