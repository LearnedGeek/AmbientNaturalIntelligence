using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Tests for the Epistemic Grounding prompt structure (Apr 10, 2026).
/// Verifies that tier-partitioned memory pools render into the correct prompt
/// sections ([FACTS] / [INTERIOR]) and that the model receives explicit
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

        user.Should().Contain("[FACTS]");
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
        user.Should().Contain("[FACTS]");
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
    public void LeanConversation_G5_YourWorldBlock_SurfacesRecentWorldExperiences()
    {
        // G.5 (2026-06-11) — the lean conversation prompt now includes a
        // [YOUR WORLD] block sourced from snapshot.RecentWorldExperiences.
        // This is the SELF-WORLD recall channel: when Mark asks about her day
        // or her reading, she has substrate to draw on rather than confabulating.
        // Issue #92 §G.5.
        var snapshot = BuildSnapshot();
        snapshot.RecentWorldExperiences = new List<MemoryRecord>
        {
            new() { Content = "shelved a stack of Tana French novels this afternoon", Type = MemoryType.Semantic },
            new() { Content = "the regular with the grey coat came in for her Nora Roberts fix", Type = MemoryType.Semantic },
        };
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        user.Should().Contain("[YOUR WORLD]",
            "G.5: the SELF-WORLD recall block surfaces in the lean conversation prompt");
        user.Should().Contain("Tana French",
            "the recent world experience content surfaces in the block");
        user.Should().Contain("Do NOT quote phrases verbatim",
            "non-quote framing tells the model these are reference material, not text to lift");
    }

    [Fact]
    public void LeanConversation_G5_YourWorldBlock_AbsentWhenNoWorldExperiences()
    {
        // G.5 (2026-06-11) — block stays silent when there's no world substrate.
        var snapshot = BuildSnapshot();  // RecentWorldExperiences default-empty
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        user.Should().NotContain("[YOUR WORLD]",
            "block is silent when no recent world experiences are populated");
    }

    [Fact]
    public void LeanConversation_G5_YourWorldBlock_CapsAtThree()
    {
        // G.5 (2026-06-11) — only the three most-recent world experiences
        // surface to bound prompt size.
        var manyExperiences = Enumerable.Range(1, 10)
            .Select(i => new MemoryRecord
            {
                Content = $"world-experience-{i}",
                Type    = MemoryType.Semantic,
            })
            .ToList();
        var snapshot = BuildSnapshot();
        snapshot.RecentWorldExperiences = manyExperiences;
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(snapshot, thread);

        user.Should().Contain("world-experience-1");
        user.Should().Contain("world-experience-3");
        user.Should().NotContain("world-experience-4",
            "G.5: cap at 3 to bound prompt size");
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

        user.Should().Contain("[FACTS]");
        user.Should().Contain("Mark is a software consultant");
    }

    [Fact]
    public void ConversationReply_WithInterior_RendersDirectionShapeNotVerbatim()
    {
        // G.2 (2026-06-11): [INTERIOR] no longer surfaces raw MemoryRecord
        // content. It now renders a direction-shape line from EmotionalState
        // + DominantRegister via PromptBuilder.ComposeInteriorDirection.
        // The InteriorContext memory list is no longer consulted at the
        // prompt-construction layer (specific recall flows through the
        // G.5 retrieval channel when needed).
        //
        // This test pins: verbatim MemoryRecord content does NOT appear in
        // the prompt — that was the empirical anchor for the verbatim
        // recycling class (#92 §A).
        var snapshot = BuildSnapshot(interior: new List<MemoryRecord>
        {
            Interior("I noticed I've been softer today"),
        });
        snapshot.EmotionalState = new EmotionalState
        {
            Warmth = 0.85f, WarmthBaseline = 0.60f,  // +0.25 → "warmth elevated"
        };
        snapshot.DominantRegister = "Tenderness";
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);

        user.Should().Contain("[INTERIOR]",
            "the section header still appears so the model can scan for it");
        user.Should().Contain("interior right now",
            "G.2 direction-shape body present");
        user.Should().Contain("warmth elevated",
            "direction summarizes felt-state from EmotionalState divergence");
        user.Should().Contain("tenderness",
            "dominant register surfaces as part of the direction");
        user.Should().NotContain("I noticed I've been softer today",
            "G.2 invariant: raw MemoryRecord content MUST NOT appear in [INTERIOR] — that was the verbatim recycling vector");
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
        system.Should().Contain("[INTERIOR]");
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

        user.Should().Contain("[FACTS]");
        user.Should().Contain("Mark teaches evening classes at WCTC");
    }

    [Fact]
    public void OutreachMessage_NoFacts_SignalsNullResult()
    {
        var snapshot = BuildSnapshot();

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, "thought of him", "warm");

        user.Should().Contain("[FACTS]");
        user.Should().Contain("no grounding memories retrieved");
    }

    [Fact]
    public void OutreachMessage_WithInterior_RendersDirectionShapeNotVerbatim()
    {
        // G.2 (2026-06-11) — same direction-shape treatment for outreach as
        // for conversation reply. Empirical anchor: 2026-06-11 07:06 outreach
        // where "imperfect vs preterite" 5/24 confabulation resurfaced via
        // this exact verbatim-memory surfacing path. #92 §G.2.
        var snapshot = BuildSnapshot(interior: new List<MemoryRecord>
        {
            Interior("the bookstore felt quiet today"),
        });
        snapshot.EmotionalState = new EmotionalState
        {
            Warmth = 0.65f, WarmthBaseline = 0.50f,  // +0.15 → "warmth elevated"
            Energy = 0.35f, EnergyBaseline = 0.50f,  // -0.15 → "energy low"
        };

        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            snapshot, "thinking of him", "gentle");

        user.Should().Contain("[INTERIOR]",
            "the section header still appears so the model can scan for it");
        user.Should().Contain("interior right now",
            "G.2 direction-shape body present");
        user.Should().NotContain("the bookstore felt quiet today",
            "G.2 invariant: raw MemoryRecord content MUST NOT appear in [INTERIOR]");
    }

    // ─── Cross-cutting: fact vs interior separation ───────────────────────────

    [Fact]
    public void FactsAndInteriorAppearInSeparateSections()
    {
        // G.2 (2026-06-11): [FACTS] still surfaces verbatim Mark-asserted
        // grounding (that's the recall channel for Mark-domain truth).
        // [INTERIOR] no longer surfaces verbatim memory — it's a direction
        // line. Sections remain separated; the test now verifies the new
        // shape — verbatim Fact content present, verbatim Interior content
        // ABSENT, both section headers present.
        var snapshot = BuildSnapshot(
            facts: new List<MemoryRecord> { Fact("Mark's daughters are Mia and Karen") },
            interior: new List<MemoryRecord> { Interior("I love when they laugh together") });
        snapshot.EmotionalState = new EmotionalState
        {
            Warmth = 0.80f, WarmthBaseline = 0.60f,  // +0.20 → triggers direction
        };
        var thread = new ConversationThread();

        var (_, user) = PromptBuilder.BuildConversationReplyPrompt(snapshot, thread);

        var factsPosition = user.IndexOf("[FACTS]");
        var interiorPosition = user.IndexOf("[INTERIOR]");

        factsPosition.Should().BeGreaterThan(-1, "[FACTS] section present");
        interiorPosition.Should().BeGreaterThan(-1, "[INTERIOR] section present (direction-shape body)");
        factsPosition.Should().NotBe(interiorPosition, "sections remain separated");

        user.Should().Contain("Mark's daughters are Mia and Karen",
            "G.2 invariant: Mark-asserted [FACTS] content remains verbatim — that's the recall channel");
        user.Should().NotContain("I love when they laugh together",
            "G.2 invariant: Interior MemoryRecord content does NOT appear — verbatim recycling vector closed");
    }
}
