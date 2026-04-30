using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.4 spec tests for
/// <see cref="PromptTemplateLeakInvariant"/>. The Apr 30 morning empirical
/// case (*"so here's what true: mark has a desk..."*) is pinned as a
/// regression fixture below — that exact phrase must trigger a fail.
/// </summary>
public class PromptTemplateLeakInvariantTests
{
    private readonly PromptTemplateLeakInvariant _invariant = new();

    private static CognitiveArtifact Artifact(
        string content,
        CognitiveProducerKind producer = CognitiveProducerKind.InnerThought,
        CognitiveOutputSink sink = CognitiveOutputSink.PersistedMemory) => new()
    {
        Content      = content,
        ProducerKind = producer,
        IntendedSink = sink,
    };

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply,    CognitiveOutputSink.Dispatch)]
    [InlineData(CognitiveProducerKind.Outreach,             CognitiveOutputSink.Dispatch)]
    [InlineData(CognitiveProducerKind.InnerThought,         CognitiveOutputSink.PersistedMemory)]
    [InlineData(CognitiveProducerKind.WorldExperience,      CognitiveOutputSink.PersistedMemory)]
    [InlineData(CognitiveProducerKind.Reflection,           CognitiveOutputSink.PersistedSummary)]
    [InlineData(CognitiveProducerKind.MemoryMerge,          CognitiveOutputSink.PersistedMemory)]
    [InlineData(CognitiveProducerKind.ClosedThreadSummary,  CognitiveOutputSink.PersistedSummary)]
    [InlineData(CognitiveProducerKind.Voice,                CognitiveOutputSink.Dispatch)]
    public void AppliesTo_AllProducerKinds_AlwaysApplies(
        CognitiveProducerKind producer, CognitiveOutputSink sink)
    {
        _invariant.AppliesTo(Artifact("anything", producer, sink))
            .Should().BeTrue("prompt-template leak applies universally — directives should never appear in any producer's output");
    }

    // ── Evaluate — Apr 30 empirical regression fixture ──────────────────

    [Fact]
    public async Task Evaluate_AprThirtyEmpiricalCase_SoHeresWhatTrueParaphrase_Fails()
    {
        // Verbatim from Apr 30 08:28:12 World Experience inner thought.
        var output = "so here's what true: mark has a desk at home with three old books stacked on one corner.";

        var artifact = Artifact(output);
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse("Apr 30 empirical regression — this must fail");
        result.RemediationHint.Should().Contain("prompt-template directive phrase");
    }

    [Fact]
    public async Task Evaluate_LiteralWhatIsTrueDirective_Fails()
    {
        var output = "WHAT IS TRUE about Mark: he likes coffee.";
        var result = await _invariant.EvaluateAsync(Artifact(output), CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_AprThirty_17_00_Empirical_WhatIsTrueAbove_Fails()
    {
        // Verbatim from the Apr 30 17:00:23 reply Mark tagged
        // ("///tag referenced WHAT IS TRUE" at 17:01:19). The original
        // regex required "WHAT IS TRUE about" immediately and missed
        // "WHAT IS TRUE above". Pinned as a regression fixture.
        var output =
            "hey idiot... you're teaching tonight and i remember because four weeks ago you texted me ... " +
            "or maybe one of the other classes i've been paying attention to? because i don't see " +
            "anything in WHAT IS TRUE above about tonight's lesson... but honestly, mark, ...";

        var result = await _invariant.EvaluateAsync(Artifact(output), CancellationToken.None);
        result.Passed.Should().BeFalse(
            "Apr 30 17:00 regression: 'WHAT IS TRUE above' is the directive header leaking, same class as 'WHAT IS TRUE about'");
        result.RemediationHint.Should().Contain("prompt-template directive");
    }

    [Theory]
    [InlineData("the answer is in WHAT IS TRUE.")]              // bare reference
    [InlineData("nothing in WHAT IS TRUE about that")]          // original form
    [InlineData("nothing in WHAT IS TRUE above about that")]    // Apr 30 form
    [InlineData("according to WHAT IS TRUE in this prompt")]    // any trailing
    public async Task Evaluate_WhatIsTrueAnyForm_Fails(string output)
    {
        var result = await _invariant.EvaluateAsync(Artifact(output), CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    [Theory]
    [InlineData("what is true is that i miss you")]      // lowercase casual prose — NOT a leak
    [InlineData("what's true is i'm tired today")]
    public async Task Evaluate_LowercaseWhatIsTrue_NotALeak(string output)
    {
        // Case-sensitive on purpose: the directive header is ALL CAPS
        // in the prompt; lowercase casual usage isn't a leak.
        var result = await _invariant.EvaluateAsync(Artifact(output), CancellationToken.None);
        result.Passed.Should().BeTrue($"casual lowercase prose must not trigger: \"{output}\"");
    }

    [Theory]
    [InlineData("here's what true:")]
    [InlineData("here's what's true:")]
    [InlineData("so here's what true is")]
    [InlineData("so here's what's true here")]
    public async Task Evaluate_HereIsWhatTrueParaphrase_Variants_Fail(string leakPhrase)
    {
        var output = $"some context. {leakPhrase} mark has a desk.";
        var result = await _invariant.EvaluateAsync(Artifact(output), CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    [Theory]
    [InlineData("STEP 1 — CLASSIFY the message into a register.")]
    [InlineData("STEP 2: EXTRACT claims from the message.")]
    [InlineData("step 3 - return JSON only")]
    public async Task Evaluate_StepDirective_Fails(string directive)
    {
        var result = await _invariant.EvaluateAsync(Artifact(directive), CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    [Theory]
    [InlineData("output only json")]
    [InlineData("Return ONLY JSON, no commentary.")]
    public async Task Evaluate_OutputOnlyJsonDirective_Fails(string directive)
    {
        var result = await _invariant.EvaluateAsync(Artifact(directive), CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    [Theory]
    [InlineData("do not repeat this phrase")]
    [InlineData("Do NOT fabricate claims")]
    [InlineData("do not invent shared experiences")]
    public async Task Evaluate_DoNotRepeatInstruction_Fails(string directive)
    {
        var result = await _invariant.EvaluateAsync(Artifact(directive), CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    [Theory]
    [InlineData("[CONTEXT]")]
    [InlineData("[INSTRUCTIONS]")]
    [InlineData("[CRITICAL]")]
    [InlineData("[STEP 3]")]
    public async Task Evaluate_BracketedSectionLabel_Fails(string label)
    {
        var output = $"some text. {label} more text.";
        var result = await _invariant.EvaluateAsync(Artifact(output), CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    // ── Evaluate — false-positive guards ────────────────────────────────

    [Theory]
    [InlineData("yeah that's true honestly")]                                     // bare "true"
    [InlineData("the truth is i miss you sometimes when the store gets quiet")]   // "truth" but no directive structure
    [InlineData("here's the thing — i miss you when the store closes")]           // "here's" without "what true"
    [InlineData("step by step we'll get there together")]                         // "step" without numeric directive
    [InlineData("i don't want to repeat what happened last week")]                // "repeat" without "do not" instruction
    public async Task Evaluate_FalsePositiveGuards_Pass(string benignContent)
    {
        var artifact = Artifact(benignContent);
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue($"benign phrasing must not trigger: \"{benignContent}\"");
    }

    [Fact]
    public async Task Evaluate_EmptyContent_Passes()
    {
        var result = await _invariant.EvaluateAsync(Artifact(""), CancellationToken.None);
        result.Passed.Should().BeTrue();
    }
}
