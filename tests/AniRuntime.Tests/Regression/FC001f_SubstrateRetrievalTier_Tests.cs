using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// ARCHITECTURAL PIN test (INVERTED 2026-05-14 from SPEC FAIL → PIN PASS).
///
/// FC-001 was closed 2026-05-14 as misnamed. The original framing treated
/// "Ani's prior dispatched message reaching the reply path" as a substrate-
/// routing problem; architecture review re-classified it as conversation
/// history (chat-history channel, not Facts-tier substrate).
///
/// Episodic records — including Ani's prior dispatched outreaches — SHOULD
/// NOT appear in Facts-tier search. That's the correct architectural
/// separation. Routing Episodic into Facts would have created the FC-004
/// epistemic-asymmetry problem (Ani-prior-claims surfacing at same level as
/// Mark-asserted facts).
///
/// This test now PINS the correct architectural separation: Facts-tier
/// search returns ONLY Facts. Episodic stays in Episodic. The reply path
/// reaches prior dispatched content via chat history (Ollama `history`
/// parameter), not via Facts substrate routing. The actual windshield-class
/// gap is FC-010 (no continuation/walkback primitive at the producer
/// layer), which is downstream and unrelated to this layer.
///
/// TEST CATEGORY: PIN. Method name ends in `_Pin`. PASS = current
/// architectural separation is intact. FAIL would mean someone changed the
/// retrieval to surface Episodic in Facts-tier search — that's a
/// conversation, not a bug fix.
///
/// `RegressionOpen` trait removed; this test runs in the default suite.
/// </summary>
public class FC001f_SubstrateRetrievalTier_Tests : IDisposable
{
    private readonly Mock<IOllamaClient> _mockOllama = new();
    private readonly SqliteMemoryService _memory;

    // Deterministic embedding: every input produces the same unit vector.
    // This is a test stub — production uses nomic-embed-text. With a constant
    // embedding, cosine similarity between any two records is 1.0, so tier
    // filtering becomes the dominant signal. That is exactly what we want
    // to test: the tier-filter behavior, NOT cosine ranking.
    private static readonly float[] StubEmbedding = MakeStubEmbedding();

    private const string SyntheticInbound  = "FC001f-FIXTURE: synthetic question about a prop-windshield-W and fabricated-badge-Z";
    private const string AniPriorOutreach  = "FC001f-FIXTURE: Ani's prior outreach about finding a fabricated-badge-Z on her prop-windshield-W";
    private const string UnrelatedFact     = "FC001f-FIXTURE: an unrelated Facts-tier record about a completely-different-topic-Q";

    public FC001f_SubstrateRetrievalTier_Tests()
    {
        _mockOllama
            .Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StubEmbedding);

        var dbName = $"ani-fc001f-{Guid.NewGuid():N}";
        var options = Options.Create(new AniOptions
        {
            MemoryDbPath               = dbName,
            // Disable thresholds for this test — we're testing the tier filter,
            // not threshold filtering. Pass 0.0 minCosine so every match
            // crosses the floor.
            MinCosineThresholdComposition = 0.0,
            MinCosineThresholdVerifier    = 0.0,
        });
        _memory = new SqliteMemoryService(
            options,
            NullLogger<SqliteMemoryService>.Instance,
            _mockOllama.Object);
    }

    public void Dispose() => _memory.Dispose();

    /// <summary>
    /// FC-001f — PIN (inverted 2026-05-14): Episodic records SHOULD NOT
    /// appear in Facts-tier search. That's the correct architectural
    /// separation. Routing Episodic into Facts would create the FC-004
    /// epistemic-asymmetry problem (Ani's prior conversational claims
    /// surfacing at the same epistemic level as Mark-asserted Facts).
    ///
    /// `SearchByTierAsync(query, EpistemicTier.Facts, ...)` filters by
    /// `provenance = 'Facts'` at the SQL layer (`SqliteMemoryService.cs`).
    /// Episodic records are structurally excluded — by design.
    ///
    /// The reply path reaches prior dispatched content via Ollama chat
    /// history (the `history` parameter), not via Facts-tier retrieval.
    /// The May 12 windshield parrot proved this empirically — the model
    /// COULD reproduce its own prior outreach via chat history; the gap
    /// was that it had no producer-side continuation/walkback primitive
    /// (that's FC-010, an unrelated layer).
    ///
    /// PASS = architectural separation intact. FAIL = someone changed the
    /// retrieval to mix tiers, which would be a conversation, not a bug.
    /// </summary>
    [Fact]
    public async Task FC001f_AniPriorOutreach_AsEpisodicRecord_DoesNotLeakIntoFactsTierSearch_Pin()
    {
        // ── Arrange ───────────────────────────────────────────────────────
        // Seed: Ani's prior outreach as Episodic (matches OutreachPhase.cs:502
        // production persistence). High importance + recent so any
        // composite-score consumer would surface it if not filtered out.
        var aniOutreach = new MemoryRecord
        {
            Id          = Guid.NewGuid(),
            Type        = MemoryType.Episodic,
            Content     = AniPriorOutreach,
            Importance  = 0.7f,
            OccurredAt  = DateTimeOffset.UtcNow.AddMinutes(-15),
            CreatedAt   = DateTimeOffset.UtcNow.AddMinutes(-15),
            Embedding   = StubEmbedding,
            Provenance  = EpistemicTier.Episodic,
        };
        await _memory.SaveAsync(aniOutreach);

        // Control: a Facts-tier record that's irrelevant — would be returned
        // by a Facts-tier search but is NOT what the reply needs to ground on.
        var unrelatedFact = new MemoryRecord
        {
            Id          = Guid.NewGuid(),
            Type        = MemoryType.Semantic,
            Content     = UnrelatedFact,
            Importance  = 0.5f,
            OccurredAt  = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedAt   = DateTimeOffset.UtcNow.AddDays(-1),
            Embedding   = StubEmbedding,
            Provenance  = EpistemicTier.Facts,
        };
        await _memory.SaveAsync(unrelatedFact);

        // ── Act ───────────────────────────────────────────────────────────
        // Production reply path: ContextBuilder.cs:147 calls SearchByTierAsync
        // with EpistemicTier.Facts. Use the same call pattern.
        var results = await _memory.SearchByTierAsync(
            SyntheticInbound,
            EpistemicTier.Facts,
            topK: 5,
            ct: CancellationToken.None);

        // ── Assert (PIN, inverted 2026-05-14) ────────────────────────────
        // Facts-tier search MUST NOT return Episodic records. This is the
        // correct architectural separation. Routing Episodic into the
        // [FACTS] block would create the FC-004 epistemic-asymmetry
        // problem. The reply path reaches Ani's prior dispatched content
        // via chat history, not via this substrate channel.
        results.Should().NotContain(s => s.Record.Id == aniOutreach.Id,
            "Episodic records (Ani's prior dispatched content) must STAY OUT " +
            "of Facts-tier search. Mixing tiers at the retrieval boundary " +
            "would surface Ani-prior-claims at the same epistemic level as " +
            "Mark-asserted Facts, which is exactly the FC-004 failure mode. " +
            "The reply path reaches prior dispatched content via Ollama chat " +
            "history, not via Facts substrate routing. (If this PIN ever fails, " +
            "someone has changed the architecture without updating this pin — " +
            "that's a conversation, not a bug fix.)");

        // The unrelated Facts-tier record IS legitimately returned — confirms
        // the search itself is functional; only the tier filter is at play.
        results.Should().Contain(s => s.Record.Id == unrelatedFact.Id,
            "Facts-tier search must still surface legitimate Facts-tier records " +
            "(this is the positive control — proves the search is functional " +
            "and only the provenance filter is excluding Episodic).");
    }

    private static float[] MakeStubEmbedding()
    {
        // nomic-embed-text emits 768-dim vectors. Match the dimension so the
        // SqliteMemoryService length-check passes. Use a unit vector with
        // equal components → cosine to itself is 1.0; cosine to any other
        // identical vector is 1.0. We're not testing cosine ranking here.
        const int dim = 768;
        var vec = new float[dim];
        var component = (float)(1.0 / Math.Sqrt(dim));
        for (var i = 0; i < dim; i++) vec[i] = component;
        return vec;
    }
}
