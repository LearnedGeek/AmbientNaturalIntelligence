using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Tests for the Epistemic Grounding prompt structure (Apr 10, 2026).
/// Verifies that tier-partitioned memory pools render into the correct prompt
/// sections (WHAT IS TRUE / YOUR INTERIOR) and that the model receives explicit
/// architectural permission to express uncertainty when no grounding is retrieved.
///
/// See docs/spec/design/ANI-Epistemic-Grounding-Architecture.md for the design.
/// </summary>
public class EpistemicGroundingPromptTests
{
    private static ContextSnapshot BuildSnapshot(
        List<MemoryRecord>? facts = null,
        List<MemoryRecord>? interior = null,
        string contactName = "Mark",
        string characterName = "Ani")
    {
        return new ContextSnapshot
        {
            CharacterState = new CharacterStateDoc
            {
                Name = characterName,
                PrimaryContactName = contactName,
                CoreTraits = new List<string> { "warm", "curious", "wry" },
            },
            DesireState = new DesireState(),
            EmotionalState = new EmotionalState(),
            GroundedFacts = facts ?? new List<MemoryRecord>(),
            InteriorContext = interior ?? new List<MemoryRecord>(),
        };
    }

    private static MemoryRecord Fact(string content) => new()
    {
        Content = content,
        Type = MemoryType.Semantic,
        Provenance = EpistemicTier.Facts,
    };

    private static MemoryRecord Interior(string content) => new()
    {
        Content = content,
        Type = MemoryType.InnerThought,
        Provenance = EpistemicTier.Interior,
    };

    // ─── BuildLeanConversationPrompt ──────────────────────────────────────────

    [Fact]
    public void LeanConversation_WithFacts_IncludesWhatIsTrueSection()
    {
        var snapshot = BuildSnapshot(facts: new List<MemoryRecord>
        {
            Fact("Mark teaches at WCTC"),
            Fact("Mark's gym partner is Sarah"),
        });
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        user.Should().Contain("WHAT IS TRUE");
        user.Should().Contain("Mark teaches at WCTC");
        user.Should().Contain("Mark's gym partner is Sarah");
    }

    [Fact]
    public void LeanConversation_NoFacts_IncludesNullResultSignal()
    {
        var snapshot = BuildSnapshot();
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        // Post-Apr-10 hardening: "nothing specific retrieved" replaces the old wording.
        // The test verifies the null-result signal is present regardless of exact phrasing.
        user.Should().Contain("WHAT IS TRUE");
        user.Should().Contain("nothing specific retrieved");
    }

    [Fact]
    public void LeanConversation_InstructionAdjacentToReplyAsk()
    {
        // Apr 10 hardening: the constraint must be positioned IN the user message,
        // adjacent to the reply ask, not buried at the bottom of system rules.
        // Model attention decays with distance — adjacency is what makes the
        // constraint stick.
        var snapshot = BuildSnapshot();
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        user.Should().Contain("CRITICAL");
        user.Should().Contain("coworker, student, client, meeting, project");
        user.Should().Contain("Don't invent");
        user.Should().Contain("full latitude");
    }

    [Fact]
    public void LeanConversation_CapsFactsAtSix()
    {
        var manyFacts = Enumerable.Range(1, 20).Select(i => Fact($"fact {i}")).ToList();
        var snapshot = BuildSnapshot(facts: manyFacts);
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        user.Should().Contain("fact 1");
        user.Should().Contain("fact 6");
        user.Should().NotContain("fact 7");
    }

    // ─── BuildConversationReplyPrompt ─────────────────────────────────────────

    [Fact]
    public void ConversationReply_WithFacts_RendersWhatIsTrueSection()
    {
        var snapshot = BuildSnapshot(facts: new List<MemoryRecord>
        {
            Fact("Mark is a software consultant"),
        });
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);

        user.Should().Contain("WHAT IS TRUE");
        user.Should().Contain("Mark is a software consultant");
    }

    [Fact]
    public void ConversationReply_WithInterior_RendersYourInteriorSection()
    {
        var snapshot = BuildSnapshot(interior: new List<MemoryRecord>
        {
            Interior("I noticed I've been softer today"),
        });
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);

        user.Should().Contain("YOUR INTERIOR");
        user.Should().Contain("I noticed I've been softer today");
    }

    [Fact]
    public void ConversationReply_NoFacts_GivesPermissionToSayIDontKnow()
    {
        var snapshot = BuildSnapshot();
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);

        user.Should().Contain("no grounding memories retrieved");
        user.Should().Contain("avoid asserting specifics");
    }

    [Fact]
    public void ConversationReply_SystemRule_DistinguishesMarkDomainFromAniDomain()
    {
        var snapshot = BuildSnapshot();
        var thread = new ConversationThread();

        var (system, _) = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);

        system.Should().Contain("Only assert facts about Mark's life");
        system.Should().Contain("YOUR INTERIOR");
        system.Should().Contain("full creative latitude");
    }

    // ─── BuildOutreachMessagePrompt ───────────────────────────────────────────

    [Fact]
    public void OutreachMessage_WithFacts_RendersWhatIsTrueSection()
    {
        var snapshot = BuildSnapshot(facts: new List<MemoryRecord>
        {
            Fact("Mark teaches evening classes at WCTC"),
        });

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, "thinking of him teaching tonight", "warm, wanted to check in");

        user.Should().Contain("WHAT IS TRUE");
        user.Should().Contain("Mark teaches evening classes at WCTC");
    }

    [Fact]
    public void OutreachMessage_NoFacts_SignalsNullResult()
    {
        var snapshot = BuildSnapshot();

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, "thought of him", "warm");

        user.Should().Contain("WHAT IS TRUE");
        user.Should().Contain("no grounding memories retrieved");
    }

    [Fact]
    public void OutreachMessage_WithInterior_RendersYourInteriorSection()
    {
        var snapshot = BuildSnapshot(interior: new List<MemoryRecord>
        {
            Interior("the bookstore felt quiet today"),
        });

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, "thinking of him", "gentle");

        user.Should().Contain("YOUR INTERIOR");
        user.Should().Contain("the bookstore felt quiet today");
    }

    // ─── Cross-cutting: fact vs interior separation ───────────────────────────

    [Fact]
    public void FactsAndInteriorAppearInSeparateSections()
    {
        var snapshot = BuildSnapshot(
            facts: new List<MemoryRecord> { Fact("Mark's daughters are Mia and Karen") },
            interior: new List<MemoryRecord> { Interior("I love when they laugh together") });
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);

        var factsPosition = user.IndexOf("WHAT IS TRUE");
        var interiorPosition = user.IndexOf("YOUR INTERIOR");

        factsPosition.Should().BeGreaterThan(-1);
        interiorPosition.Should().BeGreaterThan(-1);
        factsPosition.Should().NotBe(interiorPosition);
        // Facts and Interior are in different sections — content doesn't blend.
        user.Should().Contain("Mark's daughters are Mia and Karen");
        user.Should().Contain("I love when they laugh together");
    }
}
