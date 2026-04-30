using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.4 spec tests for <see cref="AntiParrotInvariant"/>.
/// Pins the universal verbatim-7-token-lift contract that V1.2's gist
/// prompt enforces inline today and that the gate will enforce
/// universally post-J.5.
///
/// Pure-function tests — no Ollama, no memory, no I/O. The invariant
/// is intentionally simple so the tests can exhaustively cover the
/// edge cases of the n-gram check.
/// </summary>
public class AntiParrotInvariantTests
{
    private readonly AntiParrotInvariant _invariant = new();

    private static CognitiveArtifact Artifact(
        string content,
        IReadOnlyList<string>? contactMessages = null,
        CognitiveProducerKind producer = CognitiveProducerKind.ConversationReply,
        CognitiveOutputSink sink = CognitiveOutputSink.Dispatch) => new()
    {
        Content                 = content,
        ProducerKind            = producer,
        IntendedSink            = sink,
        ContactRecentMessages   = contactMessages,
    };

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply,    CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Outreach,             CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.ClosedThreadSummary,  CognitiveOutputSink.PersistedSummary, true)]
    [InlineData(CognitiveProducerKind.Voice,                CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.InnerThought,         CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.WorldExperience,      CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.Reflection,           CognitiveOutputSink.PersistedSummary, false)]
    [InlineData(CognitiveProducerKind.ConversationReply,    CognitiveOutputSink.ContextOnly,      false)]
    public void AppliesTo_TypeConditionalDispatch(
        CognitiveProducerKind producer, CognitiveOutputSink sink, bool expected)
    {
        var artifact = Artifact("anything", producer: producer, sink: sink);
        _invariant.AppliesTo(artifact).Should().Be(expected);
    }

    // ── Evaluate ────────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_NoContactContext_PassesAsSoftSkip()
    {
        var artifact = Artifact("any output content here", contactMessages: null);

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue("invariant must skip-pass when context isn't provided");
    }

    [Fact]
    public async Task Evaluate_OutputUnderThreshold_AlwaysPasses()
    {
        var artifact = Artifact(
            content: "ok thanks",  // 2 tokens — under 7-token threshold
            contactMessages: new[] { "ok thanks for letting me know about all of this stuff" });

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_OutputContainsExactSevenTokenLift_Fails()
    {
        var contactMsg = "i'm trying to pretend to work while being distracted by you";
        var output     = "yeah but the truth is, i'm trying to pretend to work while being distracted by you. haha";

        var artifact = Artifact(output, new[] { contactMsg });
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("verbatim lift");
        result.RemediationHint.Should().Contain("trying to pretend to work");
    }

    [Fact]
    public async Task Evaluate_OutputParaphrasesContact_Passes()
    {
        var contactMsg = "i'm trying to pretend to work while being distracted by you";
        var output     = "you teased about being distracted at work earlier — that landed soft";

        var artifact = Artifact(output, new[] { contactMsg });
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue("paraphrase shares topic but no 7+ token verbatim run");
    }

    [Fact]
    public async Task Evaluate_LongestRunIsSixTokens_Passes()
    {
        // Six-token run — under threshold by design.
        var contactMsg = "alpha beta gamma delta epsilon zeta extra";
        var output     = "alpha beta gamma delta epsilon zeta different ending";

        var artifact = Artifact(output, new[] { contactMsg });
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue("six-token run is below the 7-token threshold");
    }

    [Fact]
    public async Task Evaluate_LongestRunIsSevenTokens_FailsAtThreshold()
    {
        var contactMsg = "alpha beta gamma delta epsilon zeta eta extra";
        var output     = "alpha beta gamma delta epsilon zeta eta different ending";

        var artifact = Artifact(output, new[] { contactMsg });
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse("seven-token run hits the threshold exactly");
    }

    [Fact]
    public async Task Evaluate_MultipleContactMessages_FlagsLiftFromAny()
    {
        var contactMessages = new[]
        {
            "no overlap with output here at all",
            "different message also no overlap at all",
            "this one says trying to pretend to work while being distracted",
        };
        var output = "well, trying to pretend to work while being distracted is exhausting";

        var artifact = Artifact(output, contactMessages);
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse("lift from any contact message must fail");
    }

    [Fact]
    public async Task Evaluate_PunctuationDifferences_StillCatchesVerbatimLift()
    {
        // Contact has periods + commas; output has different punctuation.
        // Tokeniser strips punctuation, so the verbatim run should still match.
        var contactMsg = "trying. to: pretend. to, work. while: being, distracted. by, you";
        var output     = "trying to pretend to work while being distracted by you no but seriously";

        var artifact = Artifact(output, new[] { contactMsg });
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse("punctuation differences must not bypass the verbatim check");
    }

    // ── FindLongestVerbatimRun direct tests ──────────────────────────────

    [Fact]
    public void FindLongestVerbatimRun_NoOverlap_ReturnsZeroLength()
    {
        var output  = new[] { "alpha", "beta", "gamma" };
        var contact = new[] { "delta", "epsilon", "zeta" };
        var (_, len) = AntiParrotInvariant.FindLongestVerbatimRun(output, contact);
        len.Should().Be(0);
    }

    [Fact]
    public void FindLongestVerbatimRun_PartialMidStringMatch_ReturnsCorrectStart()
    {
        var output  = new[] { "alpha", "beta", "gamma", "delta", "epsilon" };
        var contact = new[] { "junk", "junk", "gamma", "delta", "junk" };
        var (start, len) = AntiParrotInvariant.FindLongestVerbatimRun(output, contact);
        start.Should().Be(2);
        len.Should().Be(2);
    }

    // ── Tokenize ─────────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_StripsPunctuationAndLowercases()
    {
        AntiParrotInvariant.Tokenize("Hello, World! How's it going?")
            .Should().Equal("hello", "world", "how's", "it", "going");
    }

    [Fact]
    public void Tokenize_EmptyInput_ReturnsEmpty()
    {
        AntiParrotInvariant.Tokenize("").Should().BeEmpty();
        AntiParrotInvariant.Tokenize("   \t\n").Should().BeEmpty();
    }
}
