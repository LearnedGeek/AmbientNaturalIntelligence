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
/// at the <b>general SearchAsync layer</b> (orthogonal to FC-001f at
/// SearchByTierAsync). Together FC-001f and FC-001g map the full retrieval
/// surface for the reply path's substrate construction.
///
/// THE LAYER:
///   <c>ContextBuilder.cs:93</c> calls <c>_search.SearchAsync(query, topK: 5, ct, enforceOriginQuota: true)</c>
///   which is the general scored-search path (no tier filter at the SQL
///   level). This populates <c>relevantMem</c> separate from the Facts-tier
///   pool. Whether an Ani Episodic outreach reaches this path is a distinct
///   question from whether it reaches GroundedFacts (FC-001f).
///
/// THE SPEC:
///   When the inbound query is semantically related to an Ani-dispatched
///   Episodic outreach, the general <c>SearchAsync</c> path SHOULD return
///   that Episodic record so the reply path's snapshot has access to it
///   in SOME form — even if not in [FACTS] block specifically.
///
/// WHY THIS MATTERS:
///   If SearchAsync DOES return Episodic (PASS), then FC-001's fix space
///   becomes: "the data is retrievable via SearchAsync but not surfaced in
///   the right snapshot field; route it through to GroundedFacts or render
///   a separate [PRIOR ANI] block."
///   If SearchAsync DOES NOT return Episodic (FAIL), the retrieval surface
///   itself is the binding constraint.
///   Either outcome is precise information for tomorrow's architecture
///   discussion.
///
/// TEST CATEGORY: SPEC.
/// </summary>
public class FC001g_GeneralSearchAsync_Tests : IDisposable
{
    private readonly Mock<IOllamaClient> _mockOllama = new();
    private readonly SqliteMemoryService _memory;

    private static readonly float[] StubEmbedding = MakeStubEmbedding();
    private const string SyntheticInbound  = "FC001g-FIXTURE: synthetic question about a prop-windshield-W and fabricated-badge-Z";
    private const string AniPriorOutreach  = "FC001g-FIXTURE: Ani's prior outreach about finding a fabricated-badge-Z on her prop-windshield-W";

    public FC001g_GeneralSearchAsync_Tests()
    {
        _mockOllama
            .Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StubEmbedding);

        var dbName = $"ani-fc001g-{Guid.NewGuid():N}";
        var options = Options.Create(new AniOptions
        {
            MemoryDbPath                  = dbName,
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
    /// FC-001g — SPEC: a recent Ani Episodic outreach should be returned by
    /// the general SearchAsync path when the query is semantically related.
    /// This is the orthogonal layer to FC-001f. Together they map the full
    /// retrieval surface for FC-001.
    /// </summary>
    [Fact]
    public async Task FC001g_AniPriorOutreach_AsEpisodicRecord_AppearsInGeneralSearchAsync_Spec()
    {
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

        var results = await _memory.SearchAsync(SyntheticInbound, topK: 10, CancellationToken.None);

        results.Should().Contain(r => r.Id == aniOutreach.Id,
            "the general SearchAsync path should return Ani's prior Episodic outreach " +
            "when the query is semantically related. This is the orthogonal retrieval " +
            "surface to SearchByTierAsync(Facts) tested by FC-001f. PASS here means the " +
            "data IS retrievable via SearchAsync — fix space becomes routing it through " +
            "to a snapshot field PromptBuilder renders. FAIL here means SearchAsync also " +
            "structurally excludes Episodic (which would be surprising; document if so).");
    }

    private static float[] MakeStubEmbedding()
    {
        const int dim = 768;
        var vec = new float[dim];
        var component = (float)(1.0 / Math.Sqrt(dim));
        for (var i = 0; i < dim; i++) vec[i] = component;
        return vec;
    }
}
