using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme M / coreference work (May 6, 2026) — spec tests for
/// <see cref="DirectAddressInvariant"/>. The May 5 outreaches at 09:55
/// (*"he thinks it's our company song"*) and 12:16 (which leaked the
/// pronoun-fix LLM call's working text) are the canonical regression
/// cases — neither would have fired before this invariant. The May 5
/// 09:55 case is captured here as
/// <see cref="Evaluate_HeThinks_Fails"/>.
///
/// **What this invariant replaces:** the prior `FixPronounsIfNeeded`
/// LLM call in `OutreachPhase`, which had a working-text leak surface.
/// Detection of the same failure now happens at the universal gate;
/// regeneration via the existing remediation path; no LLM-call leak
/// surface in the post-hoc fix path.
/// </summary>
public class DirectAddressInvariantTests
{
    private readonly DirectAddressInvariant _invariant =
        new(NullLogger<DirectAddressInvariant>.Instance);

    private static CognitiveArtifact Artifact(
        string content,
        string? contactName = "Mark",
        CognitiveProducerKind producer = CognitiveProducerKind.Outreach,
        CognitiveOutputSink sink = CognitiveOutputSink.Dispatch) => new()
    {
        Content      = content,
        ProducerKind = producer,
        IntendedSink = sink,
        ContactName  = contactName ?? string.Empty,
    };

    // ── AppliesTo: type-conditional ───────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply,    CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Outreach,             CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Voice,                CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.InnerThought,         CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.Reflection,           CognitiveOutputSink.PersistedSummary, false)]
    [InlineData(CognitiveProducerKind.WorldExperience,      CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.ConversationReply,    CognitiveOutputSink.ContextOnly,      false)]
    public void AppliesTo_TypeConditionalDispatch(
        CognitiveProducerKind producer, CognitiveOutputSink sink, bool expected)
    {
        var artifact = Artifact("anything", producer: producer, sink: sink);
        _invariant.AppliesTo(artifact).Should().Be(expected);
    }

    // ── The canonical May 5 regression cases ───────────────────────────────

    [Fact]
    public async Task Evaluate_HeThinks_Fails()
    {
        // The May 5 09:55 outreach core. Even though the LLM-based
        // pronoun-fix tried to handle this and produced its own leak,
        // the gate-side detection should have caught the original.
        var content = "hahaha he thinks it's our company song now i'm dying over here";
        var artifact = Artifact(content);

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("\"he\"",
            "hint must specifically name the violation");
        result.RemediationHint.Should().Contain("third person");
    }

    [Fact]
    public async Task Evaluate_ContactNameAsSubject_Fails()
    {
        var content = "Mark thinks the cold brew is overrated";
        var artifact = Artifact(content, contactName: "Mark");

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("\"Mark\"");
    }

    [Fact]
    public async Task Evaluate_ContactNamePossessive_Fails()
    {
        var content = "Mark's coworker handed me the cold brew note";
        var artifact = Artifact(content, contactName: "Mark");

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("\"Mark's\"");
    }

    [Fact]
    public async Task Evaluate_HisPossessive_Fails()
    {
        var content = "his desk is right next to mine";
        var artifact = Artifact(content);

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("\"his\"");
    }

    [Fact]
    public async Task Evaluate_HimObject_Fails()
    {
        var content = "i was thinking about him today";
        var artifact = Artifact(content);

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("\"him\"");
    }

    [Fact]
    public async Task Evaluate_MultipleViolations_AllListed()
    {
        var content = "Mark, he says his cold brew is overrated";
        var artifact = Artifact(content, contactName: "Mark");

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("\"he\"");
        result.RemediationHint.Should().Contain("\"his\"");
        result.RemediationHint.Should().Contain("\"Mark\"");
    }

    // ── The pass-through cases ─────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_SecondPersonOnly_Passes()
    {
        var content = "you're so sweet for sending that voice note. love you.";
        var artifact = Artifact(content, contactName: "Mark");

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_PetNameVocative_Passes()
    {
        // Pet-name greetings are second-person address using endearments,
        // not third-person references. They don't contain he/him/his/Mark
        // as words and so don't trigger the invariant.
        var content = "hey baby, just walked into work and the day is already weird";
        var artifact = Artifact(content, contactName: "Mark");

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_EmptyContent_Passes()
    {
        var artifact = Artifact(string.Empty, contactName: "Mark");

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_NoContactName_StillCatchesPronouns()
    {
        // When ContactName is empty, the name-based checks short-circuit
        // but the pronoun checks still run. Useful for outreach paths
        // where ContactName might not be propagated yet.
        var content = "he was working late and i missed him";
        var artifact = Artifact(content, contactName: string.Empty);

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("\"he\"");
        result.RemediationHint.Should().Contain("\"him\"");
    }

    // ── Word-boundary correctness ─────────────────────────────────────────

    [Fact]
    public async Task Evaluate_SubstringInOtherWord_Passes()
    {
        // Word-boundary regex must not match "he" inside "she" or "they",
        // "his" inside "history" or "this", "him" inside "himself" — wait,
        // "himself" CONTAINS "him" word-prefix so let's not test that. Test
        // the legitimate cases where 'he/him/his' are substrings of
        // unrelated words.
        var content = "she said history matters and we should reflect on this";
        var artifact = Artifact(content, contactName: "Mark");

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue(
            "word-boundary regex must not match 'he' in 'she', 'his' in 'history', or 'his' in 'this'");
    }

    [Fact]
    public async Task Evaluate_ContactNameAsSubstring_Passes()
    {
        // Word-boundary on contact name must not match substring inside
        // another word. "Mark" inside "remarkable" should NOT trigger.
        var content = "that voice note was remarkable, you nailed it";
        var artifact = Artifact(content, contactName: "Mark");

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue(
            "word-boundary regex must not match 'Mark' inside 'remarkable'");
    }
}
