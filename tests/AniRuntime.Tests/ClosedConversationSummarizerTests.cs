using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Vibe Loop V1.2 spec tests. Pin the contract of
/// <see cref="ClosedConversationSummarizer"/> as the producer of
/// <see cref="ClosedConversationRecord"/>.
///
/// Strict-mock discipline (Theme K): every <see cref="IOllamaClient"/>
/// call the test expects is set up explicitly. Loose mocks would let
/// register-classification calls silently default to empty strings,
/// masking the prevalence-vector contract that V1.5's retrieval-bias
/// function will depend on.
///
/// Pure aggregation helpers (BuildPrevalenceVector / ComputeDelta /
/// ComputeValence / Tokenize / ExtractTopicKeywords) are tested
/// directly via InternalsVisibleTo — they're deterministic and the
/// contract is easier to read at the helper level than through a
/// full SummariseAsync round-trip.
/// </summary>
public class ClosedConversationSummarizerTests
{
    private readonly Mock<IOllamaClient> _mockOllama = new(MockBehavior.Strict);

    private ClosedConversationSummarizer BuildSummarizer() => new(
        _mockOllama.Object,
        NullLogger<ClosedConversationSummarizer>.Instance);

    private static ConversationThread BuildThread(params (string role, string content)[] turns)
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        var thread = new ConversationThread
        {
            Id            = Guid.NewGuid(),
            StartedAt     = t0,
            LastMessageAt = t0.AddMinutes(turns.Length),
            InitiatedBy   = Roles.Mark,
            IsActive      = false,
        };
        for (var i = 0; i < turns.Length; i++)
        {
            thread.Messages.Add(new ConversationMessage
            {
                Role    = turns[i].role,
                Content = turns[i].content,
                SentAt  = t0.AddMinutes(i),
            });
        }
        return thread;
    }

    private static string RegisterJson(string register) => $"{{\"register\":\"{register}\"}}";

    // ===== Helper-level tests (pure logic, no Ollama) =====

    [Fact]
    public void BuildPrevalenceVector_EmptyInput_ReturnsAllZeroVector()
    {
        var v = ClosedConversationSummarizer.BuildPrevalenceVector(Array.Empty<string>());

        v.Should().HaveCount(9);
        v.Values.Should().AllSatisfy(x => x.Should().Be(0f));
    }

    [Fact]
    public void BuildPrevalenceVector_OneRegisterEachTurn_NormalisesToOne()
    {
        var v = ClosedConversationSummarizer.BuildPrevalenceVector(
            new[] { "Tenderness", "Tenderness", "Playfulness", "Playfulness" });

        v["Tenderness"].Should().BeApproximately(0.5f, 0.0001f);
        v["Playfulness"].Should().BeApproximately(0.5f, 0.0001f);
        v["Longing"].Should().Be(0f);
        v.Values.Sum().Should().BeApproximately(1.0f, 0.0001f);
    }

    [Fact]
    public void BuildPrevalenceVector_UnclassifiedTurnsDoNotInflateAnyRegister()
    {
        var v = ClosedConversationSummarizer.BuildPrevalenceVector(
            new[] { "Tenderness", "Unclassified", "Unclassified", "Unclassified" });

        v["Tenderness"].Should().BeApproximately(0.25f, 0.0001f, "1 of 4 turns was Tenderness");
        v.Values.Sum().Should().BeApproximately(0.25f, 0.0001f, "Unclassified does not project onto any register");
    }

    [Fact]
    public void ComputeValence_PositiveOnlyDelta_ReturnsPositiveScalar()
    {
        var delta = ClosedConversationSummarizer.Registers.ToDictionary(r => r, _ => 0f);
        delta["Tenderness"]  = +0.3f;
        delta["Playfulness"] = +0.2f;

        var v = ClosedConversationSummarizer.ComputeValence(delta);
        v.Should().BeApproximately(+0.5f, 0.0001f);
    }

    [Fact]
    public void ComputeValence_NegativeOnlyDelta_ReturnsNegativeScalar()
    {
        var delta = ClosedConversationSummarizer.Registers.ToDictionary(r => r, _ => 0f);
        delta["Frustration"] = +0.4f;
        delta["Wistful"]     = +0.2f;

        var v = ClosedConversationSummarizer.ComputeValence(delta);
        v.Should().BeApproximately(-0.6f, 0.0001f);
    }

    [Fact]
    public void ComputeValence_DesireExcludedFromProjection()
    {
        var delta = ClosedConversationSummarizer.Registers.ToDictionary(r => r, _ => 0f);
        delta["Desire"] = +0.5f; // context-dependent; should not contribute to scalar

        var v = ClosedConversationSummarizer.ComputeValence(delta);
        v.Should().Be(0f, "Desire is held out of the valence projection per V1.0 design");
    }

    [Fact]
    public void ComputeValence_ClampedTo_NegativeOne_PositiveOne_Range()
    {
        var saturatedPos = ClosedConversationSummarizer.Registers.ToDictionary(r => r, _ => 0f);
        foreach (var r in ClosedConversationSummarizer.PositiveRegisters)
            saturatedPos[r] = +1.0f;

        var saturatedNeg = ClosedConversationSummarizer.Registers.ToDictionary(r => r, _ => 0f);
        foreach (var r in ClosedConversationSummarizer.NegativeRegisters)
            saturatedNeg[r] = +1.0f;

        ClosedConversationSummarizer.ComputeValence(saturatedPos).Should().Be(+1.0f);
        ClosedConversationSummarizer.ComputeValence(saturatedNeg).Should().Be(-1.0f);
    }

    [Fact]
    public void ComputeDelta_AfterMinusBefore_PreservesDirection()
    {
        var before = ClosedConversationSummarizer.Registers.ToDictionary(r => r, _ => 0f);
        before["Frustration"] = 1.0f;

        var after = ClosedConversationSummarizer.Registers.ToDictionary(r => r, _ => 0f);
        after["Tenderness"] = 1.0f;

        var delta = ClosedConversationSummarizer.ComputeDelta(after, before);
        delta["Frustration"].Should().BeApproximately(-1.0f, 0.0001f);
        delta["Tenderness"].Should().BeApproximately(+1.0f, 0.0001f);
    }

    [Fact]
    public void SplitInHalf_OddCount_FirstHalfShorter()
    {
        var (first, second) = ClosedConversationSummarizer.SplitInHalf(new[] { "A", "B", "C", "D", "E" });
        first.Should().Equal("A", "B");
        second.Should().Equal("C", "D", "E");
    }

    [Fact]
    public void ExtractTopicKeywords_ReturnsTopFrequencyTerms_StopwordFiltered()
    {
        var thread = BuildThread(
            (Roles.Mark, "the dentist appointment was a relief, the dentist said no cavities"),
            (Roles.Ani, "oh good, the dentist news is the best kind"),
            (Roles.Mark, "yeah dentist visits used to scare me but not anymore"));

        var keywords = ClosedConversationSummarizer.ExtractTopicKeywords(thread.Messages, topN: 3);

        keywords.Should().Contain("dentist");
        keywords.Should().NotContain("the");
        keywords.Should().NotContain("was");
    }

    [Fact]
    public void ParseRegister_CanonicalCasing_NormalisedFromLowercase()
    {
        ClosedConversationSummarizer.ParseRegister("{\"register\":\"tenderness\"}")
            .Should().Be("Tenderness");
    }

    [Fact]
    public void ParseRegister_UnknownRegister_FallsBackToUnclassified()
    {
        ClosedConversationSummarizer.ParseRegister("{\"register\":\"Joy\"}")
            .Should().Be("Unclassified");
    }

    [Fact]
    public void ParseRegister_Malformed_FallsBackToUnclassified()
    {
        ClosedConversationSummarizer.ParseRegister("definitely not json")
            .Should().Be("Unclassified");
    }

    [Fact]
    public void SanitiseGist_CollapsesWhitespace_AndCapsLength()
    {
        ClosedConversationSummarizer.SanitiseGist("  hi\n\nthere\twith    extra  whitespace  ")
            .Should().Be("hi there with extra whitespace");

        var huge = new string('a', 1000);
        ClosedConversationSummarizer.SanitiseGist(huge).Length.Should().Be(600);
    }

    [Fact]
    public void SanitiseGist_EmptyInput_ReturnsPlaceholder()
    {
        ClosedConversationSummarizer.SanitiseGist("   ").Should().Be("Mark and Ani spoke briefly.");
    }

    [Fact]
    public void BuildGistPrompt_SystemContainsAntiParrotConstraint()
    {
        var thread = BuildThread((Roles.Mark, "hi"));
        var (sys, _) = ClosedConversationSummarizer.BuildGistPrompt(thread);

        sys.Should().Contain("DO NOT quote", "anti-parrot constraint must be explicit");
        sys.Should().Contain("7 or more consecutive words", "the verbatim threshold is part of the prompt contract");
        sys.Should().Contain("paraphrase", "structural paraphrase guarantee");
    }

    // ===== End-to-end SummariseAsync tests via strict mocks =====

    [Fact]
    public async Task SummariseAsync_EmptyThread_Throws()
    {
        var summarizer = BuildSummarizer();
        var thread = new ConversationThread { Id = Guid.NewGuid() };

        var act = () => summarizer.SummariseAsync(thread);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SummariseAsync_HappyPath_PopulatesAllFields()
    {
        var thread = BuildThread(
            (Roles.Mark, "i had a hard day at work and felt overwhelmed"),
            (Roles.Ani, "that sounds heavy. i wish i could be there with you"),
            (Roles.Mark, "thanks. just hearing that helps a lot, you always know what to say"),
            (Roles.Ani, "i'm just glad you told me. i love being someone you can land on"));

        // Per-turn classification calls — strict, in order.
        // Mark turn 1 → Frustration, Mark turn 2 → Tenderness
        // Ani turn 1 → Tenderness, Ani turn 2 → Tenderness
        var classificationCallCount = 0;
        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                classificationCallCount++;
                return classificationCallCount switch
                {
                    1 => RegisterJson("Frustration"),
                    2 => RegisterJson("Tenderness"),
                    3 => RegisterJson("Tenderness"),
                    4 => RegisterJson("Tenderness"),
                    _ => RegisterJson("Unclassified"),
                };
            });

        // Gist call
        _mockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float?>()))
            .ReturnsAsync("Mark unloaded a hard day and Ani held the moment gently; he relaxed.");

        // Embedding call
        _mockOllama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 0.1f, 0.2f, 0.3f });

        var record = (await BuildSummarizer().SummariseAsync(thread)).Content;

        record.ThreadId.Should().Be(thread.Id);
        record.TurnCount.Should().Be(4);
        record.Gist.Should().NotBeNullOrWhiteSpace();
        record.Embedding.Should().Equal(0.1f, 0.2f, 0.3f);

        record.MarkRegister["Frustration"].Should().BeApproximately(0.5f, 0.0001f, "1 of 2 mark turns was Frustration");
        record.MarkRegister["Tenderness"].Should().BeApproximately(0.5f, 0.0001f);
        record.AniRegister["Tenderness"].Should().BeApproximately(1.0f, 0.0001f, "both ani turns were Tenderness");
        record.AniRegister["Frustration"].Should().Be(0f);

        // Outcome signal: ani went Tenderness → Tenderness; delta should be ~zero.
        record.OutcomeSignalSeedVector.Should().HaveCount(9);
        record.OutcomeSignalValence.Should().BeApproximately(0f, 0.0001f);

        record.TopicKeywords.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SummariseAsync_AniTurnsShiftFromFrustrationToTenderness_ProducesPositiveValence()
    {
        var thread = BuildThread(
            (Roles.Mark, "you keep getting that wrong"),
            (Roles.Ani,  "i'm so tired of this, i can't get anything right"),
            (Roles.Ani,  "but actually wait — i hear you. let me try again"),
            (Roles.Ani,  "ok this feels better. thank you for not giving up on me"),
            (Roles.Ani,  "i love that we work things out together"));

        var classificationCallCount = 0;
        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                classificationCallCount++;
                // Order: Mark turn (1), then Ani turns (2..5).
                return classificationCallCount switch
                {
                    1 => RegisterJson("Frustration"),    // Mark
                    2 => RegisterJson("Frustration"),    // Ani turn 1 (first half)
                    3 => RegisterJson("Frustration"),    // Ani turn 2 (first half)
                    4 => RegisterJson("Tenderness"),     // Ani turn 3 (second half)
                    5 => RegisterJson("Tenderness"),     // Ani turn 4 (second half)
                    _ => RegisterJson("Unclassified"),
                };
            });
        _mockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ReturnsAsync("After Mark's correction, Ani moved from frustration to genuine repair.");
        _mockOllama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 0.0f });

        var record = (await BuildSummarizer().SummariseAsync(thread)).Content;

        record.OutcomeSignalSeedVector["Tenderness"].Should().BeApproximately(+1.0f, 0.0001f,
            "second half is 100% Tenderness, first half is 0% Tenderness");
        record.OutcomeSignalSeedVector["Frustration"].Should().BeApproximately(-1.0f, 0.0001f,
            "second half is 0% Frustration, first half is 100% Frustration");
        record.OutcomeSignalValence.Should().BeGreaterThan(0.5f,
            "Frustration→Tenderness is the canonical positive movement");
    }

    [Fact]
    public async Task SummariseAsync_EmbeddingFailure_ReturnsRecordWithNullEmbedding()
    {
        var thread = BuildThread((Roles.Mark, "hello"), (Roles.Ani, "hi"));

        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RegisterJson("Curiosity"));
        _mockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ReturnsAsync("They greeted each other.");
        _mockOllama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama unreachable"));

        var record = (await BuildSummarizer().SummariseAsync(thread)).Content;

        record.Gist.Should().NotBeNullOrWhiteSpace();
        record.Embedding.Should().BeNull("embedding failure must NOT block record production");
    }

    [Fact]
    public async Task SummariseAsync_GistLLMFailure_FallsBackToHeuristicGistAndStillEmbeds()
    {
        var thread = BuildThread((Roles.Mark, "hey"), (Roles.Ani, "hi"));

        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RegisterJson("Curiosity"));
        _mockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ThrowsAsync(new HttpRequestException("ollama unreachable"));
        _mockOllama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 0.42f });

        var record = (await BuildSummarizer().SummariseAsync(thread)).Content;

        record.Gist.Should().Contain("Mark and Ani", "heuristic gist names both speakers without echoing content");
        record.Gist.Should().NotContain("hey",
            "heuristic-gist must not echo any verbatim message content (anti-parrot guarantee on the failure path)");
        record.Embedding.Should().Equal(0.42f);
    }

    [Fact]
    public async Task SummariseAsync_OnlyOneSpeakerHasTurns_OtherSpeakerVectorIsAllZero()
    {
        var thread = BuildThread(
            (Roles.Mark, "first"),
            (Roles.Mark, "second"),
            (Roles.Mark, "third"));

        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RegisterJson("Curiosity"));
        _mockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ReturnsAsync("Mark spoke alone.");
        _mockOllama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { 0.0f });

        var record = (await BuildSummarizer().SummariseAsync(thread)).Content;

        record.MarkRegister["Curiosity"].Should().BeApproximately(1.0f, 0.0001f);
        record.AniRegister.Values.Sum().Should().Be(0f, "Ani had no turns; her register vector should be all zeros");
        record.OutcomeSignalValence.Should().Be(0f, "no Ani movement to project");
    }
}
