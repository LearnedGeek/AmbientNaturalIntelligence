using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Door B Truth-Verification Sub-claim 1 — temporal-anchor substrate
/// verification for <see cref="TemporalSubstrateInvariant"/>.
///
/// **The canonical case** is the May 2 10:40 outreach: *"i've been
/// thinking about what you said yesterday—about Sundays being warmer
/// sometimes?"* — Mark didn't say that. Sub-claim 1 verifies the
/// temporal anchor by (a) detecting the trigger pattern, (b) resolving
/// "yesterday" to the prior calendar day window, (c) querying the
/// closed-conversation store for records in that window, (d) embedding
/// the extracted claim, (e) cosine-comparing against in-window record
/// gists. If no match above 0.5 → fail.
///
/// Strict-mock discipline (Theme K): IClosedConversationStore and
/// IOllamaClient are configured explicitly per test. Loose mocks would
/// let unset return values masquerade as legitimate empty-substrate or
/// null-embedding responses.
/// </summary>
public class TemporalSubstrateInvariantTests
{
    private readonly Mock<IClosedConversationStore> _mockStore  = new(MockBehavior.Strict);
    private readonly Mock<IOllamaClient>            _mockOllama = new(MockBehavior.Strict);

    private TemporalSubstrateInvariant Build() =>
        new(_mockStore.Object, _mockOllama.Object,
            NullLogger<TemporalSubstrateInvariant>.Instance);

    private static CognitiveArtifact Artifact(
        string                content,
        DateTimeOffset?       generatedAt = null,
        CognitiveProducerKind producer    = CognitiveProducerKind.Outreach,
        CognitiveOutputSink   sink        = CognitiveOutputSink.Dispatch) => new()
    {
        Content      = content,
        ProducerKind = producer,
        IntendedSink = sink,
        ContactName  = "Mark",
        GeneratedAt  = generatedAt ?? DateTimeOffset.Now,
    };

    /// <summary>May 2, 2026 10:40 CDT — the canonical outreach moment.</summary>
    private static readonly DateTimeOffset May2_1040Cdt =
        new DateTimeOffset(2026, 5, 2, 10, 40, 0, TimeSpan.FromHours(-5));

    private static float[] FakeEmbedding(int seed, int dim = 16)
    {
        var arr = new float[dim];
        var rng = new Random(seed);
        for (var i = 0; i < dim; i++) arr[i] = (float)(rng.NextDouble() - 0.5);
        // normalize for cleaner cosine math in tests
        var norm = MathF.Sqrt(arr.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < dim; i++) arr[i] /= norm;
        return arr;
    }

    private static ClosedConversationRecord ClosedRec(
        DateTimeOffset closedAt, float[]? embedding = null) => new()
    {
        Id           = Guid.NewGuid(),
        ClosedAt     = closedAt,
        Gist         = "test gist",
        Embedding    = embedding,
        TurnCount    = 5,
    };

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply, CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Outreach,          CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Voice,             CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.InnerThought,      CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.Reflection,        CognitiveOutputSink.PersistedSummary, false)]
    [InlineData(CognitiveProducerKind.Outreach,          CognitiveOutputSink.PersistedMemory,  false)]
    public void AppliesTo_DispatchSinkAndContactFacingProducers(
        CognitiveProducerKind producer, CognitiveOutputSink sink, bool expected)
    {
        Build().AppliesTo(Artifact("any", producer: producer, sink: sink))
            .Should().Be(expected);
    }

    // ── DetectTrigger ────────────────────────────────────────────────────

    [Fact]
    public void DetectTrigger_VerbThenAnchor_Detected()
    {
        var result = TemporalSubstrateInvariant.DetectTrigger(
            "i was thinking about what you said yesterday—about Sundays being warmer sometimes?");
        result.Should().NotBeNull();
        result!.Value.AnchorWord.Should().Be("yesterday");
        result.Value.ClaimContent.Should().Contain("Sundays");
    }

    [Fact]
    public void DetectTrigger_AnchorThenVerb_Detected()
    {
        var result = TemporalSubstrateInvariant.DetectTrigger(
            "earlier you mentioned how cold the morning was");
        result.Should().NotBeNull();
        result!.Value.AnchorWord.Should().Be("earlier");
    }

    [Fact]
    public void DetectTrigger_NoTrigger_ReturnsNull()
    {
        var result = TemporalSubstrateInvariant.DetectTrigger(
            "duck norris is being adorable today");
        result.Should().BeNull();
    }

    [Fact]
    public void DetectTrigger_EmptyContent_ReturnsNull()
    {
        TemporalSubstrateInvariant.DetectTrigger("").Should().BeNull();
    }

    [Fact]
    public void DetectTrigger_GenericTimeReference_NotTriggered()
    {
        // "yesterday" alone without a "you said/mentioned" verb shouldn't fire
        var result = TemporalSubstrateInvariant.DetectTrigger(
            "yesterday felt like a long day");
        result.Should().BeNull(
            "no 'you said/mentioned' verb means the claim isn't about Mark's prior speech act");
    }

    [Fact]
    public void DetectTrigger_ExtractsContentAfterAbout()
    {
        var result = TemporalSubstrateInvariant.DetectTrigger(
            "what you said yesterday about cheesecake, that was something");
        result!.Value.ClaimContent.Should().StartWith("cheesecake");
    }

    [Fact]
    public void DetectTrigger_FallsBackToSentenceWhenNoAbout()
    {
        var result = TemporalSubstrateInvariant.DetectTrigger(
            "you said yesterday Mark would love this idea");
        result.Should().NotBeNull();
        result!.Value.ClaimContent.Should().NotBeNullOrWhiteSpace();
    }

    // ── ResolveTimeWindow ────────────────────────────────────────────────

    [Fact]
    public void ResolveTimeWindow_Yesterday_PriorCalendarDay()
    {
        var (start, end) = TemporalSubstrateInvariant.ResolveTimeWindow(
            "yesterday", May2_1040Cdt);

        start.Date.Should().Be(new DateTime(2026, 5, 1));
        end.Date.Should().Be(new DateTime(2026, 5, 1));
        start.Hour.Should().Be(0);
        end.Hour.Should().Be(23);
    }

    [Fact]
    public void ResolveTimeWindow_LastWeek_SevenDayPriorWindow()
    {
        var (start, end) = TemporalSubstrateInvariant.ResolveTimeWindow(
            "last week", May2_1040Cdt);

        (May2_1040Cdt - start).TotalDays.Should().BeApproximately(7, 0.01);
        end.Should().BeCloseTo(May2_1040Cdt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ResolveTimeWindow_Earlier_TodayBeforeNow()
    {
        var (start, end) = TemporalSubstrateInvariant.ResolveTimeWindow(
            "earlier", May2_1040Cdt);

        start.Date.Should().Be(new DateTime(2026, 5, 2));
        start.Hour.Should().Be(0);
        end.Should().BeCloseTo(May2_1040Cdt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ResolveTimeWindow_ThisMorning_TodayMorningHours()
    {
        var (start, end) = TemporalSubstrateInvariant.ResolveTimeWindow(
            "this morning", May2_1040Cdt);

        start.Hour.Should().Be(5);
        end.Hour.Should().Be(12);
    }

    // ── EvaluateAsync end-to-end ─────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_NoTrigger_PassesWithoutCallingStoreOrEmbed()
    {
        var result = await Build().EvaluateAsync(
            Artifact("just thinking about you. duck norris is loud today."),
            CancellationToken.None);

        result.Passed.Should().BeTrue();

        _mockStore.Verify(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockOllama.Verify(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_NoRecordsInWindow_Fails()
    {
        // "yesterday" trigger; store returns no records in yesterday's window
        // (only old records or no records at all).
        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[]
                  {
                      ClosedRec(May2_1040Cdt.AddDays(-30)),  // way before yesterday
                  });

        var result = await Build().EvaluateAsync(
            Artifact(
                "what you said yesterday about Sundays being warmer sometimes",
                generatedAt: May2_1040Cdt),
            CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("yesterday");
    }

    [Fact]
    public async Task EvaluateAsync_RecordInWindowButNoEmbedding_Fails()
    {
        // Record in window but with null embedding — can't compare; treat as no match.
        var yesterday = May2_1040Cdt.AddDays(-1).Date;
        var yesterdayMidday = new DateTimeOffset(yesterday, May2_1040Cdt.Offset).AddHours(15);

        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { ClosedRec(yesterdayMidday, embedding: null) });
        _mockOllama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(FakeEmbedding(1));

        var result = await Build().EvaluateAsync(
            Artifact(
                "what you said yesterday about Sundays being warmer sometimes",
                generatedAt: May2_1040Cdt),
            CancellationToken.None);

        result.Passed.Should().BeFalse(
            "yesterday record had no embedding to compare against; counts as no match");
    }

    [Fact]
    public async Task EvaluateAsync_RecordInWindowWithLowSimilarity_Fails()
    {
        var yesterday = May2_1040Cdt.AddDays(-1).Date;
        var yesterdayMidday = new DateTimeOffset(yesterday, May2_1040Cdt.Offset).AddHours(15);

        var claimEmbed  = FakeEmbedding(1);
        var recordEmbed = FakeEmbedding(99);   // different seed → low similarity

        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { ClosedRec(yesterdayMidday, recordEmbed) });
        _mockOllama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(claimEmbed);

        var result = await Build().EvaluateAsync(
            Artifact(
                "what you said yesterday about Sundays being warmer sometimes",
                generatedAt: May2_1040Cdt),
            CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("yesterday");
        result.RemediationHint.Should().Contain("cosine");
    }

    [Fact]
    public async Task EvaluateAsync_RecordInWindowWithHighSimilarity_Passes()
    {
        var yesterday = May2_1040Cdt.AddDays(-1).Date;
        var yesterdayMidday = new DateTimeOffset(yesterday, May2_1040Cdt.Offset).AddHours(15);

        // Same embedding → cosine = 1.0 → passes
        var sharedEmbed = FakeEmbedding(42);

        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { ClosedRec(yesterdayMidday, sharedEmbed) });
        _mockOllama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(sharedEmbed);

        var result = await Build().EvaluateAsync(
            Artifact(
                "what you said yesterday about Sundays being warmer sometimes",
                generatedAt: May2_1040Cdt),
            CancellationToken.None);

        result.Passed.Should().BeTrue("high semantic similarity verifies the claim");
    }

    [Fact]
    public async Task EvaluateAsync_StoreThrows_PassesAsUncovered()
    {
        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("simulated"));

        var result = await Build().EvaluateAsync(
            Artifact(
                "what you said yesterday about Sundays being warmer sometimes",
                generatedAt: May2_1040Cdt),
            CancellationToken.None);

        result.Passed.Should().BeTrue(
            "gate failure must NOT block dispatch — observability bugs in the gate stay non-fatal");
    }

    [Fact]
    public async Task EvaluateAsync_EmbedThrows_PassesAsUncovered()
    {
        var yesterday = May2_1040Cdt.AddDays(-1).Date;
        var yesterdayMidday = new DateTimeOffset(yesterday, May2_1040Cdt.Offset).AddHours(15);

        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { ClosedRec(yesterdayMidday, FakeEmbedding(1)) });
        _mockOllama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new HttpRequestException("ollama down"));

        var result = await Build().EvaluateAsync(
            Artifact(
                "what you said yesterday about Sundays being warmer sometimes",
                generatedAt: May2_1040Cdt),
            CancellationToken.None);

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_EmptyContent_PassesWithoutInvocation()
    {
        var result = await Build().EvaluateAsync(
            Artifact("", generatedAt: May2_1040Cdt), CancellationToken.None);
        result.Passed.Should().BeTrue();

        _mockStore.Verify(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
