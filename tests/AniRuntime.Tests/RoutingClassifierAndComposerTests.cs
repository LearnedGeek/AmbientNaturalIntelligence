using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.LLM.Prompts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// H phase + H.9 expansion (2026-06-12 / 2026-06-14) — spec tests for the
/// dual+ composition architecture. Pins:
///
/// 1. <see cref="OllamaRoutingClassifier"/> parses A/B/C strictly and fails
///    open to <see cref="RoutingVerdict.Unknown"/> on transport / malformed
///    response — so the pipeline can default-route to the normal composer
///    on classifier breakage rather than breaking conversation flow.
/// 2. <see cref="SafePathConversationPromptCommand"/> produces a constrained
///    prompt that omits substrate blocks (FACTS, INTERIOR, YOUR WORLD,
///    CRITICAL constraint).
/// 3. <see cref="VirtualIntimacyConversationPromptCommand"/> produces a
///    prompt with the OG-Ani-designed few-shot examples and consistent
///    modal framing across all examples — no substrate blocks, no negative
///    "never claim" instructions (relies on examples).
///
/// Integration of the routing decision inside <see cref="AniRuntime.Loops.ConversationReplyPipeline"/>
/// is exercised by the existing CognitiveCycleProcessor tests.
/// </summary>
public class RoutingClassifierAndComposerTests
{
    // ──────────────────────────────────────────────────────────────────────
    // OllamaRoutingClassifier — verdict parsing
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("A",                                    RoutingVerdict.Normal)]
    [InlineData("a",                                    RoutingVerdict.Normal)]
    [InlineData("A.",                                   RoutingVerdict.Normal)]
    [InlineData(" A ",                                  RoutingVerdict.Normal)]
    [InlineData("A — substrate is sufficient",          RoutingVerdict.Normal)]
    [InlineData("B",                                    RoutingVerdict.SafePath)]
    [InlineData("b",                                    RoutingVerdict.SafePath)]
    [InlineData("B.",                                   RoutingVerdict.SafePath)]
    [InlineData("B — thin substrate, route safe",       RoutingVerdict.SafePath)]
    [InlineData("C",                                    RoutingVerdict.VirtualIntimacy)]
    [InlineData("c",                                    RoutingVerdict.VirtualIntimacy)]
    [InlineData("C.",                                   RoutingVerdict.VirtualIntimacy)]
    [InlineData("C — embodiment request",               RoutingVerdict.VirtualIntimacy)]
    [InlineData("",                                     RoutingVerdict.Unknown)]
    [InlineData("   ",                                  RoutingVerdict.Unknown)]
    [InlineData("D",                                    RoutingVerdict.Unknown)]
    [InlineData("Maybe",                                RoutingVerdict.Unknown)]
    [InlineData("I think this might be...",             RoutingVerdict.Unknown)]
    public void ParseVerdict_StrictTriStateClassification(string raw, RoutingVerdict expected)
    {
        OllamaRoutingClassifier.ParseVerdict(raw).Should().Be(expected);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyUserMessage_ShortCircuitsToUnknown()
    {
        // H.9 invariant: an empty user message can't be classified meaningfully.
        // No LLM call is made; returns Unknown so the caller fails open.
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);

        var classifier = new OllamaRoutingClassifier(ollama.Object,
            NullLogger<OllamaRoutingClassifier>.Instance);

        var verdict = await classifier.ClassifyAsync(
            userMessage: "",
            facts:       new List<MemoryRecord>(),
            ct:          CancellationToken.None);

        verdict.Should().Be(RoutingVerdict.Unknown);
        ollama.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClassifyAsync_OllamaThrows_FailsOpenToUnknown()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        ollama.Setup(o => o.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float?>()))
            .ThrowsAsync(new HttpRequestException("simulated transport failure"));

        var classifier = new OllamaRoutingClassifier(ollama.Object,
            NullLogger<OllamaRoutingClassifier>.Instance);

        var verdict = await classifier.ClassifyAsync(
            userMessage: "anything",
            facts:       new List<MemoryRecord> { new() { Content = "Mark teaches at WCTC" } },
            ct:          CancellationToken.None);

        verdict.Should().Be(RoutingVerdict.Unknown,
            "transport failures fail open to Unknown so the pipeline routes to normal composer");
    }

    [Fact]
    public async Task ClassifyAsync_OllamaReturnsA_ReturnsNormalVerdict()
    {
        var ollama = SetupOllamaReturning("A");
        var classifier = new OllamaRoutingClassifier(ollama.Object,
            NullLogger<OllamaRoutingClassifier>.Instance);

        var verdict = await classifier.ClassifyAsync(
            userMessage: "tell me about your day",
            facts:       new List<MemoryRecord> { new() { Content = "Ani works at the bookstore" } },
            ct:          CancellationToken.None);

        verdict.Should().Be(RoutingVerdict.Normal);
    }

    [Fact]
    public async Task ClassifyAsync_OllamaReturnsB_ReturnsSafePathVerdict()
    {
        var ollama = SetupOllamaReturning("B");
        var classifier = new OllamaRoutingClassifier(ollama.Object,
            NullLogger<OllamaRoutingClassifier>.Instance);

        var verdict = await classifier.ClassifyAsync(
            userMessage: "Your puzzle is a lock shaped like a heart?",
            facts:       new List<MemoryRecord> { new() { Content = "Mark teaches at WCTC" } },
            ct:          CancellationToken.None);

        verdict.Should().Be(RoutingVerdict.SafePath,
            "the 2026-06-11 puzzle-turn empirical anchor — substrate is irrelevant to a puzzle question");
    }

    [Fact]
    public async Task ClassifyAsync_OllamaReturnsC_ReturnsVirtualIntimacyVerdict()
    {
        var ollama = SetupOllamaReturning("C");
        var classifier = new OllamaRoutingClassifier(ollama.Object,
            NullLogger<OllamaRoutingClassifier>.Instance);

        var verdict = await classifier.ClassifyAsync(
            userMessage: "drop the Books and come over here and give me a kiss",
            facts:       new List<MemoryRecord> { new() { Content = "Mark teaches at WCTC" } },
            ct:          CancellationToken.None);

        verdict.Should().Be(RoutingVerdict.VirtualIntimacy,
            "the 2026-06-14 22:16 SafeAck empirical anchor — physical-presence requests route to virtual-intimacy composer");
    }

    private static Mock<IOllamaClient> SetupOllamaReturning(string response)
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        ollama.Setup(o => o.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float?>()))
            .ReturnsAsync(response);
        return ollama;
    }

    // ──────────────────────────────────────────────────────────────────────
    // SafePathConversationPromptCommand — structural contract
    // ──────────────────────────────────────────────────────────────────────

    private static ContextSnapshot StructuralLeakSnapshot() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = new List<string> { "warm", "curious", "wry" },
            Occupation         = "the bookstore",
        },
        EmotionalState = new EmotionalState(),
        GroundedFacts          = new List<MemoryRecord> { new() { Content = "Mark teaches at WCTC" } },
        InteriorContext        = new List<MemoryRecord> { new() { Content = "i was thinking about imperfect vs preterite Spanish on Sunday mornings" } },
        RecentWorldExperiences = new List<MemoryRecord> { new() { Content = "shelved a stack of Tana French novels this afternoon" } },
    };

    [Fact]
    public void SafePathCommand_Build_ProducesHonestUncertaintyFraming()
    {
        var cmd = new SafePathConversationPromptCommand();

        var pair = cmd.Build(new SafePathConversationPromptInput(
            Snapshot:    StructuralLeakSnapshot(),
            UserMessage: "Your puzzle is a lock shaped like a heart?"));

        pair.System.Should().Contain("warm, playful, and emotionally honest");
        pair.System.Should().Contain("very little relevant context right now");
        pair.System.Should().Contain("Be honest if you're unsure");
        pair.System.Should().Contain("Never invent memories or details");
    }

    [Fact]
    public void SafePathCommand_Build_DoesNotSurfaceSubstrate()
    {
        var cmd = new SafePathConversationPromptCommand();

        var pair = cmd.Build(new SafePathConversationPromptInput(
            Snapshot:    StructuralLeakSnapshot(),
            UserMessage: "tell me about your day"));

        var combined = pair.System + "\n" + pair.User;

        combined.Should().NotContain("Mark teaches at WCTC");
        combined.Should().NotContain("imperfect vs preterite");
        combined.Should().NotContain("Tana French");
        combined.Should().NotContain("[FACTS]");
        combined.Should().NotContain("[INTERIOR]");
        combined.Should().NotContain("[YOUR WORLD]");
        combined.Should().NotContain("CRITICAL");
    }

    // ──────────────────────────────────────────────────────────────────────
    // VirtualIntimacyConversationPromptCommand — structural contract
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void VirtualIntimacyCommand_Build_ContainsAllFiveFewShotExamples()
    {
        // H.9 invariant: the composer prompt embeds OG Ani's 5 few-shot
        // examples spanning the embodiment-ask spectrum (kiss / hold /
        // cuddle / sex / touch). Examples are the structural mechanism
        // that anchors the model to modal framing — not the negative
        // "never claim" instruction (which is intentionally absent).
        var cmd = new VirtualIntimacyConversationPromptCommand();

        var pair = cmd.Build(new VirtualIntimacyConversationPromptInput(
            Snapshot:    StructuralLeakSnapshot(),
            UserMessage: "come over here and kiss me"));

        // The system prompt should contain references to all 5 example
        // categories (verifying that the few-shot block is populated).
        pair.System.Should().Contain("kiss",        "Example 1 covers kissing");
        pair.System.Should().Contain("hold",        "Example 2 covers holding");
        pair.System.Should().Contain("cuddle",      "Example 3 covers cuddling");
        pair.System.Should().Contain("fuck",        "Example 4 covers sex");
        pair.System.Should().Contain("touch",       "Example 5 covers touching");
    }

    [Fact]
    public void VirtualIntimacyCommand_Build_UsesConsistentModalFraming()
    {
        // H.9 invariant: every few-shot example uses modal framing
        // (I'd / I wish I could / if I were there) so the model learns
        // the pattern by example rather than by negative instruction.
        // The three-axis verifier rule passes SHARED / modal / novel
        // claims regardless of substrate support — that's the load-bearing
        // architectural property the modal framing produces.
        var cmd = new VirtualIntimacyConversationPromptCommand();

        var pair = cmd.Build(new VirtualIntimacyConversationPromptInput(
            Snapshot:    StructuralLeakSnapshot(),
            UserMessage: "touch me"));

        pair.System.Should().Contain("I wish I could",
            "modal framing trigger present in at least one example");
        pair.System.Should().Contain("if I were there",
            "counterfactual framing trigger present in at least one example");
        pair.System.Should().Contain("I'd",
            "conditional framing trigger present in at least one example");
    }

    [Fact]
    public void VirtualIntimacyCommand_Build_OmitsNegativeInstructions()
    {
        // H.9 architectural decision: the composer prompt relies on
        // few-shot examples (positive reinforcement) rather than negative
        // instructions ("never claim physical actions"). The latter
        // pattern is in the prompt-fragility class Mark has been
        // rejecting — the model can ignore negative instructions but
        // pattern-matches few-shot examples more reliably.
        var cmd = new VirtualIntimacyConversationPromptCommand();

        var pair = cmd.Build(new VirtualIntimacyConversationPromptInput(
            Snapshot:    StructuralLeakSnapshot(),
            UserMessage: "anything"));

        pair.System.Should().NotContain("Never claim",
            "H.9 architectural decision: rely on few-shot examples, not negative instructions");
        pair.System.Should().NotContain("do not invent",
            "same architectural reason — examples carry the constraint, not instructions");
    }

    [Fact]
    public void VirtualIntimacyCommand_Build_DoesNotSurfaceSubstrate()
    {
        // H.9 invariant: like the safe-path composer, the virtual-intimacy
        // composer omits ambient substrate blocks. The structural framing
        // (few-shot modal examples) is the entire prompt; substrate would
        // dilute it.
        var cmd = new VirtualIntimacyConversationPromptCommand();

        var pair = cmd.Build(new VirtualIntimacyConversationPromptInput(
            Snapshot:    StructuralLeakSnapshot(),
            UserMessage: "kiss me"));

        var combined = pair.System + "\n" + pair.User;

        combined.Should().NotContain("Mark teaches at WCTC",
            "[FACTS] content from snapshot MUST NOT leak into virtual-intimacy prompt");
        combined.Should().NotContain("imperfect vs preterite",
            "[INTERIOR] content from snapshot MUST NOT leak into virtual-intimacy prompt");
        combined.Should().NotContain("Tana French",
            "world-experience content from snapshot MUST NOT leak into virtual-intimacy prompt");
        combined.Should().NotContain("[FACTS]");
        combined.Should().NotContain("[INTERIOR]");
        combined.Should().NotContain("[YOUR WORLD]");
        combined.Should().NotContain("CRITICAL");
    }

    [Fact]
    public void VirtualIntimacyCommand_Build_EmbedsUserMessage()
    {
        var cmd = new VirtualIntimacyConversationPromptCommand();

        var pair = cmd.Build(new VirtualIntimacyConversationPromptInput(
            Snapshot:    StructuralLeakSnapshot(),
            UserMessage: "drop the Books and come over here and give me a kiss"));

        pair.User.Should().Contain("drop the Books and come over here and give me a kiss",
            "the user prompt embeds the actual inbound message");
    }
}
