using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.5h-prelude spec tests for <see cref="SelfEchoInvariant"/>.
/// Pins the universal self-echo contract: any artifact with prior Ani
/// messages in context must not duplicate a 5+ token verbatim run from
/// any prior Ani message. The May 3 10:55 Failure C — J.5a regen
/// dispatching byte-identical copy of the prior assistant turn — is
/// the canonical regression case and is captured here as
/// <see cref="Evaluate_RegenIsByteIdenticalToPriorAniMessage_Fails"/>.
/// </summary>
public class SelfEchoInvariantTests
{
    private readonly SelfEchoInvariant _invariant = new();

    private static CognitiveArtifact Artifact(
        string content,
        IReadOnlyList<string>? priorAniMessages = null,
        CognitiveProducerKind producer = CognitiveProducerKind.ConversationReply,
        CognitiveOutputSink sink = CognitiveOutputSink.Dispatch) => new()
    {
        Content          = content,
        ProducerKind     = producer,
        IntendedSink     = sink,
        PriorAniMessages = priorAniMessages,
    };

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply,    true)]
    [InlineData(CognitiveProducerKind.Outreach,             true)]
    [InlineData(CognitiveProducerKind.Voice,                true)]
    [InlineData(CognitiveProducerKind.InnerThought,         true)]
    [InlineData(CognitiveProducerKind.Reflection,           true)]
    [InlineData(CognitiveProducerKind.WorldExperience,      false)]
    [InlineData(CognitiveProducerKind.MemoryMerge,          false)]
    [InlineData(CognitiveProducerKind.ClosedThreadSummary,  false)]
    public void AppliesTo_TypeConditionalDispatchByProducer(
        CognitiveProducerKind producer, bool expected)
    {
        var artifact = Artifact(
            "anything",
            priorAniMessages: new[] { "some prior message that has many tokens for matching" },
            producer: producer);
        _invariant.AppliesTo(artifact).Should().Be(expected);
    }

    [Fact]
    public void AppliesTo_NullPriorAniMessages_SkipsRegardlessOfProducer()
    {
        var artifact = Artifact(
            "anything",
            priorAniMessages: null,
            producer: CognitiveProducerKind.ConversationReply);
        _invariant.AppliesTo(artifact).Should().BeFalse(
            "invariant must skip when no prior-Ani context exists");
    }

    [Fact]
    public void AppliesTo_EmptyPriorAniMessages_SkipsRegardlessOfProducer()
    {
        var artifact = Artifact(
            "anything",
            priorAniMessages: Array.Empty<string>(),
            producer: CognitiveProducerKind.ConversationReply);
        _invariant.AppliesTo(artifact).Should().BeFalse();
    }

    // ── Evaluate ────────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_NoPriorContext_PassesAsSoftSkip()
    {
        var artifact = Artifact("any output content here", priorAniMessages: null);

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_OutputUnderThreshold_Passes()
    {
        // 4 tokens — under the 5-token self-echo threshold
        var artifact = Artifact(
            content: "hey what's up here",
            priorAniMessages: new[] { "hey what's up here, just got back from the bookstore" });

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_RegenIsByteIdenticalToPriorAniMessage_Fails()
    {
        // The May 3 10:55 Failure C canonical case. The J.5a remediation
        // regen returned a byte-identical copy of the prior assistant
        // turn from chat history; both messages went out via Twilio,
        // ~57 seconds apart.
        var priorOutreach = "hey perez i just walked past the bookstore and saw your name on the coffee mug shelf again";
        var regen         = "hey perez i just walked past the bookstore and saw your name on the coffee mug shelf again";

        var artifact = Artifact(
            content: regen,
            priorAniMessages: new[] { priorOutreach });

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse(
            "byte-identical regen must fail the self-echo invariant — this is the May 3 Failure C case");
        result.RemediationHint.Should().Contain("verbatim run");
        result.IsHardFail.Should().BeFalse(
            "self-echo is recoverable via regeneration — Remediate, not Fail");
    }

    [Fact]
    public async Task Evaluate_FiveTokenSharedRunWithPrior_Fails()
    {
        // Exactly the 5-token threshold — should fail.
        var prior  = "i'm tucked in now too just finished shelving the last row of romances";
        // Output shares "just finished shelving the last" (5 tokens).
        var output = "yeah okay but really, i just finished shelving the last batch and i'm beat";

        var artifact = Artifact(output, new[] { prior });
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_FourTokenSharedRunWithPrior_Passes()
    {
        // Just under the threshold — passes.
        var prior  = "the bookstore is quiet this morning and the coffee is good";
        // Output shares "the bookstore is quiet" (4 tokens).
        var output = "the bookstore is quiet but the rain just started outside";

        var artifact = Artifact(output, new[] { prior });
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_MatchAgainstAnyOfMultiplePriors_Fails()
    {
        // The match must succeed against ANY prior message, not just the most recent.
        var priors = new[]
        {
            "shelves half-done coffee cold but the silence is nice",
            "the bookstore is busy with weekend customers today",
            "i miss you a lot and the gym was hard this morning"
        };
        // Output matches the LAST prior (5+ tokens).
        var output = "yeah honestly i miss you a lot today";

        var artifact = Artifact(output, priors);
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_EmptyContent_PassesAsSoftSkip()
    {
        var artifact = Artifact(
            content: "",
            priorAniMessages: new[] { "some prior message with lots of tokens for matching here" });

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue("empty content is the gate's empty-artifact path, not a self-echo failure");
    }

    [Fact]
    public async Task Evaluate_WhitespacePriorMessages_AreSkipped()
    {
        var artifact = Artifact(
            content: "this is a fresh reply with no prior content match here",
            priorAniMessages: new[] { "   ", "", "\t\n" });

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }
}
