using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.LLM.Prompts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// H phase (2026-06-12) — spec tests for the substrate-thinness routing
/// architecture (Issue #92 follow-on, dual-path composition).
///
/// **The three behaviors pinned here:**
/// 1. <see cref="OllamaRelevanceFilter"/> parses YES/NO strictly and fails
///    open to <see cref="RelevanceVerdict.Unknown"/> on transport / malformed
///    response — so the pipeline can default-route to the normal composer
///    on filter breakage rather than breaking conversation flow.
/// 2. <see cref="SafePathConversationPromptCommand"/> produces a constrained
///    prompt that omits substrate blocks (FACTS, INTERIOR, YOUR WORLD,
///    CRITICAL constraint) and includes the honest-uncertainty framing
///    that produced the good arm-C outputs in the 2026-06-11 3-arm test.
/// 3. Defensive empty-facts short-circuit: <see cref="OllamaRelevanceFilter.JudgeAsync"/>
///    returns <see cref="RelevanceVerdict.No"/> without an LLM call when
///    facts is empty — there's no question to ask the model when the
///    answer is structurally known.
///
/// Integration of the routing decision inside <see cref="AniRuntime.Loops.ConversationReplyPipeline"/>
/// is exercised by the existing CognitiveCycleProcessor tests (strict-mock
/// IOllamaClient ensures any unauthorized model call breaks the test) plus
/// production deployment under the M0_RELEVANCE_FILTER_VERDICT telemetry.
/// </summary>
public class RelevanceFilterAndSafePathTests
{
    // ──────────────────────────────────────────────────────────────────────
    // OllamaRelevanceFilter — verdict parsing
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("YES",                                  RelevanceVerdict.Yes)]
    [InlineData("yes",                                  RelevanceVerdict.Yes)]
    [InlineData("Yes",                                  RelevanceVerdict.Yes)]
    [InlineData("YES.",                                 RelevanceVerdict.Yes)]
    [InlineData("YES — substrate contains specifics.",  RelevanceVerdict.Yes)]
    [InlineData("  YES",                                RelevanceVerdict.Yes)]  // leading whitespace tolerated
    [InlineData("NO",                                   RelevanceVerdict.No)]
    [InlineData("no",                                   RelevanceVerdict.No)]
    [InlineData("No",                                   RelevanceVerdict.No)]
    [InlineData("NO.",                                  RelevanceVerdict.No)]
    [InlineData("NO — facts are off-topic.",            RelevanceVerdict.No)]
    [InlineData("",                                     RelevanceVerdict.Unknown)]
    [InlineData("   ",                                  RelevanceVerdict.Unknown)]
    [InlineData("Maybe",                                RelevanceVerdict.Unknown)]  // strict — no MAYBE branch
    [InlineData("I think the facts are partially...",   RelevanceVerdict.Unknown)]  // verbose preamble → fail open
    public void ParseVerdict_StrictBinaryClassification(string raw, RelevanceVerdict expected)
    {
        OllamaRelevanceFilter.ParseVerdict(raw).Should().Be(expected);
    }

    [Fact]
    public async Task JudgeAsync_EmptyFacts_ShortCircuitsToNoWithoutLlmCall()
    {
        // H invariant: with zero facts, the answer is structurally NO. No
        // model call is made. Strict mock breaks if the filter tries to
        // call the model anyway — that would be wasted tokens on a question
        // with a known answer.
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);

        var filter = new OllamaRelevanceFilter(ollama.Object,
            NullLogger<OllamaRelevanceFilter>.Instance);

        var verdict = await filter.JudgeAsync(
            userMessage: "Your puzzle is a lock shaped like a heart?",
            facts:       new List<MemoryRecord>(),  // empty
            ct:          CancellationToken.None);

        verdict.Should().Be(RelevanceVerdict.No,
            "structurally NO when no facts exist — no LLM call needed");
        ollama.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JudgeAsync_NullFacts_ShortCircuitsToNoWithoutLlmCall()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);

        var filter = new OllamaRelevanceFilter(ollama.Object,
            NullLogger<OllamaRelevanceFilter>.Instance);

        var verdict = await filter.JudgeAsync(
            userMessage: "anything",
            facts:       null!,
            ct:          CancellationToken.None);

        verdict.Should().Be(RelevanceVerdict.No);
        ollama.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JudgeAsync_OllamaThrows_FailsOpenToUnknown()
    {
        // H failure-mode contract: when the model call throws (transport,
        // timeout, parse error from the request layer), return Unknown so
        // the caller can fail open. NOT throw — that would break the
        // conversation flow on a single judgment-call hiccup.
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        ollama.Setup(o => o.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float?>()))
            .ThrowsAsync(new HttpRequestException("simulated transport failure"));

        var filter = new OllamaRelevanceFilter(ollama.Object,
            NullLogger<OllamaRelevanceFilter>.Instance);

        var verdict = await filter.JudgeAsync(
            userMessage: "anything",
            facts:       new List<MemoryRecord> { new() { Content = "Mark teaches at WCTC" } },
            ct:          CancellationToken.None);

        verdict.Should().Be(RelevanceVerdict.Unknown,
            "transport failures fail open to Unknown so the pipeline routes to normal composer");
    }

    [Fact]
    public async Task JudgeAsync_OllamaReturnsYes_ReturnsYesVerdict()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        ollama.Setup(o => o.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float?>()))
            .ReturnsAsync("YES");

        var filter = new OllamaRelevanceFilter(ollama.Object,
            NullLogger<OllamaRelevanceFilter>.Instance);

        var verdict = await filter.JudgeAsync(
            userMessage: "tell me about teaching",
            facts:       new List<MemoryRecord> { new() { Content = "Mark teaches at WCTC" } },
            ct:          CancellationToken.None);

        verdict.Should().Be(RelevanceVerdict.Yes);
    }

    [Fact]
    public async Task JudgeAsync_OllamaReturnsNo_ReturnsNoVerdict()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        ollama.Setup(o => o.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float?>()))
            .ReturnsAsync("NO");

        var filter = new OllamaRelevanceFilter(ollama.Object,
            NullLogger<OllamaRelevanceFilter>.Instance);

        var verdict = await filter.JudgeAsync(
            userMessage: "Your puzzle is a lock shaped like a heart?",
            facts:       new List<MemoryRecord> { new() { Content = "Mark teaches at WCTC" } },
            ct:          CancellationToken.None);

        verdict.Should().Be(RelevanceVerdict.No,
            "the 2026-06-11 puzzle-turn empirical anchor — Mark's teaching is not relevant to a puzzle question");
    }

    // ──────────────────────────────────────────────────────────────────────
    // SafePathConversationPromptCommand — structural contract
    // ──────────────────────────────────────────────────────────────────────

    private static ContextSnapshot SafePathSnapshot() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            // Substrate-laundering surfaces: these MUST NOT appear in the
            // safe-path prompt even though the snapshot has them populated.
            CoreTraits         = new List<string> { "warm", "curious", "wry" },
            Occupation         = "the bookstore",
        },
        EmotionalState = new EmotionalState(),
        // The safe-path's defining property: it does NOT consume substrate
        // (facts, interior, world experiences). Populate these to confirm
        // they don't leak through.
        GroundedFacts          = new List<MemoryRecord> { new() { Content = "Mark teaches at WCTC" } },
        InteriorContext        = new List<MemoryRecord> { new() { Content = "i was thinking about imperfect vs preterite Spanish on Sunday mornings" } },
        RecentWorldExperiences = new List<MemoryRecord> { new() { Content = "shelved a stack of Tana French novels this afternoon" } },
    };

    [Fact]
    public void SafePathCommand_Build_ProducesHonestUncertaintyFraming()
    {
        var cmd = new SafePathConversationPromptCommand();

        var pair = cmd.Build(new SafePathConversationPromptInput(
            Snapshot:    SafePathSnapshot(),
            UserMessage: "Your puzzle is a lock shaped like a heart?"));

        pair.System.Should().Contain("warm, playful, and emotionally honest",
            "the safe-path identity framing matches the 2026-06-11 3-arm-test arm C prompt");
        pair.System.Should().Contain("very little relevant context right now",
            "the model is explicitly told substrate is thin");
        pair.System.Should().Contain("Be honest if you're unsure",
            "honest-uncertainty instruction is present");
        pair.System.Should().Contain("Ask gentle questions if needed",
            "ask-back instruction is present");
        pair.System.Should().Contain("Never invent memories or details",
            "explicit anti-invention instruction");
    }

    [Fact]
    public void SafePathCommand_Build_EmbedsUserMessage()
    {
        var cmd = new SafePathConversationPromptCommand();

        var pair = cmd.Build(new SafePathConversationPromptInput(
            Snapshot:    SafePathSnapshot(),
            UserMessage: "Your puzzle is a lock shaped like a heart? I was thinking some sort of weird riddle or crazy logic puzzle.  Ha!"));

        pair.User.Should().Contain("Your puzzle is a lock shaped like a heart",
            "the user prompt embeds Mark's actual message so the model has the question to respond to");
        pair.User.Should().Contain("Reply to Mark's message",
            "explicit reply imperative");
    }

    [Fact]
    public void SafePathCommand_Build_DoesNotSurfaceSubstrate()
    {
        // H invariant: the safe-path prompt is intentionally minimal. Even
        // though the snapshot has GroundedFacts + InteriorContext +
        // RecentWorldExperiences populated, NONE of that content reaches
        // the prompt. The safe-path's purpose is honest-uncertainty;
        // surfacing substrate would fight the framing.
        var cmd = new SafePathConversationPromptCommand();

        var pair = cmd.Build(new SafePathConversationPromptInput(
            Snapshot:    SafePathSnapshot(),
            UserMessage: "tell me about your day"));

        var combined = pair.System + "\n" + pair.User;

        combined.Should().NotContain("Mark teaches at WCTC",
            "[FACTS] block content MUST NOT appear in safe-path prompt");
        combined.Should().NotContain("imperfect vs preterite",
            "[INTERIOR] content MUST NOT appear in safe-path prompt — the verbatim-leak class");
        combined.Should().NotContain("Tana French",
            "[YOUR WORLD] / recent world experience content MUST NOT appear in safe-path prompt");
        combined.Should().NotContain("[FACTS]",
            "no FACTS section header — substrate is intentionally absent");
        combined.Should().NotContain("[INTERIOR]",
            "no INTERIOR section header — substrate is intentionally absent");
        combined.Should().NotContain("[YOUR WORLD]",
            "no YOUR WORLD section header — substrate is intentionally absent");
        combined.Should().NotContain("CRITICAL",
            "no CRITICAL constraint block — the safe-path prompt itself IS the constraint");
    }
}
