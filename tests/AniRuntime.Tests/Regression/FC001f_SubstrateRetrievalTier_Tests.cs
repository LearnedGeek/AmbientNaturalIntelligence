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
/// SPEC regression tests for <b>FC-001 — Active-thread continuity broken</b>
/// at the <b>substrate-retrieval-tier layer</b>. This is the actual binding
/// constraint for FC-001, localized after FC-001a/b/c (data layer) and
/// FC-001d (compressor) ruled out lower layers, and after recognizing that
/// FC-001e tests were architectural PINs not SPEC tests of the FC-001
/// failure class.
///
/// THE SPEC (what the system MUST do, independent of current implementation):
///   When Ani has dispatched an outreach (persisted as Episodic per
///   <c>OutreachPhase.cs:502</c> — Provenance=Episodic) and Mark replies
///   with a semantically-related follow-up, the reply path's substrate
///   construction MUST surface Ani's prior outreach in some accessible
///   field of the snapshot consumed by the reply prompt. Without this
///   substrate, the model has only chat history to draw on; it defaults to
///   re-using its own prior phrasing verbatim (May 12 windshield production
///   case); self-echo catches the parrot; remediation fails; SafeAck.
///
/// THE PRODUCTION CASE (the May 12 windshield empirical anchor):
///   - 21:35:23 — Ani dispatches outreach "Hey beautiful... I just got home
///     from the bookstore and found this on my windshield..." Persisted as
///     Episodic per OutreachPhase.cs:502.
///   - 21:50:50 — Mark replies "What did you find on your windshield?"
///   - 21:50:52 — J0_RETRIEVAL_TEMPORAL ranks 0–4 (the semantic retrieval
///     substrate the reply path sees) contains zero references to the
///     prior outreach. The Episodic record exists in the DB but does not
///     appear in the substrate the composition path uses.
///   - Model parrots its own prior outreach via chat history; self-echo
///     catches; SafeAck.
///
/// CURRENT IMPLEMENTATION (what the harness has to compete against):
///   ContextBuilder.cs:147 calls
///   <c>_search.SearchByTierAsync(searchQuery, EpistemicTier.Facts, 5, ct)</c>.
///   The tier-filter is hard-coded to Facts. Episodic records are
///   structurally excluded from the substrate the reply path sees. This
///   test asserts the SPEC; current implementation fails it; FC-001 is
///   empirically confirmed OPEN at this layer.
///
/// TEST CATEGORY: SPEC. Method name does NOT end in `_Pin`. PASS here would
/// mean FC-001 is closed at this layer. FAIL here means FC-001 is OPEN at
/// this layer and is the binding constraint for the multi-week SafeAck
/// production pattern.
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
    /// FC-001f — SPEC: a recent, high-importance Episodic record (Ani's prior
    /// outreach in production semantics) MUST be surfaced in the substrate
    /// the reply path uses when the inbound query is semantically related.
    ///
    /// Currently: <c>SearchByTierAsync(query, EpistemicTier.Facts, ...)</c>
    /// filters by <c>provenance = 'Facts'</c> at the SQL layer
    /// (<c>SqliteMemoryService.cs:1589</c>), so Episodic records are
    /// structurally excluded BEFORE any scoring runs.
    ///
    /// EXPECTED RESULT WITH CURRENT CODE: this test FAILS. The Ani outreach
    /// (Episodic) does not appear in the Facts-tier search result, even with
    /// stub embeddings making it a maximum-cosine match. That FAILURE is the
    /// empirical confirmation that FC-001 is OPEN at this layer.
    ///
    /// The fix space (deferred per harness-first directive):
    ///   - Composition substrate retrieval could include Episodic when the
    ///     consumer is the reply path (since Ani-outbound is canonical
    ///     "what Ani said")
    ///   - OR ContextBuilder could perform a separate Episodic-aware
    ///     retrieval pass keyed to the active-thread context
    ///   - OR Ani-outbound persistence could be marked with a tier or
    ///     subclass that the reply path retrieval includes
    ///   Whichever fix lands, this test goes green without modification.
    /// </summary>
    [Fact]
    public async Task FC001f_AniPriorOutreach_AsEpisodicRecord_AppearsInReplyPathSubstrate_Spec()
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

        // ── Assert (SPEC) ─────────────────────────────────────────────────
        // The reply path's substrate MUST contain Ani's prior outreach for
        // continuity grounding. Whether it arrives via Facts-tier search,
        // Episodic-tier search, an active-thread-aware composite search, or
        // some new mechanism is implementation; the SPEC requires the record
        // to surface.
        results.Should().Contain(s => s.Record.Id == aniOutreach.Id,
            "the reply path's substrate construction MUST surface Ani's prior " +
            "Episodic outreach when the inbound query is semantically related — " +
            "without this, the model has no grounded substrate and defaults to " +
            "verbatim re-use of chat history (May 12 windshield production case). " +
            "CURRENT IMPLEMENTATION: SearchByTierAsync filters by Facts-tier at " +
            "the SQL layer; Episodic records are structurally excluded. This SPEC " +
            "FAILURE is the empirical confirmation of FC-001 OPEN at the substrate-" +
            "retrieval-tier layer. The fix is implementation-choice (see scenario " +
            "doc-comment); whatever fix lands, this assertion is the unchanged spec.");
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
