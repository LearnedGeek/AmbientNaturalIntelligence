using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #93 (2026-07-06) — pins the confirmation-boost multiplicative bias
/// on <see cref="EfSemanticSearchComposer.ComputeRetrievalScore"/>.
///
/// <para>
/// Empirical anchor: on the 2026-07-06 6-probe harness against the production
/// snapshot, a Kevin-thread query returned 101 Interior "Kevin's jokes still
/// on my skin"-shape records above the ~14 real Episodic Kevin conversation
/// records. Interior beat Episodic because the composite formula is additive
/// (α·cosine + β·importance + γ·recency) and Interior records had inflated
/// importance from months of laundering through the world-experience
/// generator. Retrieval treated confirmed content and unconfirmed content
/// on the same axis.
/// </para>
///
/// <para>
/// The fix is a multiplicative bump on the base composite when
/// <see cref="MemoryRecord.ConfirmedAt"/> is set. Facts + Episodic get
/// ConfirmedAt=CreatedAt at backfill; Interior stays NULL until a Mark
/// ///tag intent classifier (Issue #93 Phase 2) promotes specific records.
/// </para>
/// </summary>
public class ConfirmationBoostTests
{
    private const double DefaultBoost = 0.30;

    private static DbContextOptions<AniDbContext> InMemoryOptions() =>
        new DbContextOptionsBuilder<AniDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

    private static EfSemanticSearchComposer Composer(double? boost = null)
    {
        // Factory that never gets called — ComputeRetrievalScore does not
        // touch the DB. Same seam MemoryRepositoryValidityFilterTests uses.
        var factoryStub = new StubDbContextFactory();
        var options = Options.Create(new AniOptions
        {
            RetrievalWeightCosine       = 0.65,
            RetrievalWeightImportance   = 0.10,
            RetrievalWeightRecency      = 0.25,
            RetrievalRecencyDecayHours  = 48.0,
            RetrievalConfirmationBoost  = boost ?? DefaultBoost,
        });
        return new EfSemanticSearchComposer(
            factoryStub,
            options,
            NullLogger<EfSemanticSearchComposer>.Instance);
    }

    /// <summary>
    /// Query embedding + a record embedding that produces a known cosine.
    /// Uses a fixed unit vector so cosine is deterministic — cosine of two
    /// identical unit vectors is 1.0.
    /// </summary>
    private static (float[] query, float[] record) IdenticalUnitEmbeddings()
    {
        var e = new float[768];
        e[0] = 1.0f;
        return (e, e.ToArray());
    }

    private static MemoryRecord MakeRecord(
        DateTimeOffset? confirmedAt,
        string?         confirmedBy,
        float           importance = 0.5f,
        DecayTier       decayTier  = DecayTier.Standard,
        MemoryType      type       = MemoryType.Episodic)
    {
        var (_, rec) = IdenticalUnitEmbeddings();
        return new MemoryRecord
        {
            Id          = Guid.NewGuid(),
            Type        = type,
            Content     = "test",
            Importance  = importance,
            DecayTier   = decayTier,
            OccurredAt  = DateTimeOffset.UtcNow.AddHours(-1),
            ConfirmedAt = confirmedAt,
            ConfirmedBy = confirmedBy,
            Embedding   = rec,
        };
    }

    // Issue #97 (2026-08-27) — legacy-formula tests need EpistemicTier.Interior
    // to hit the recency-decay branch. Default MakeRecord uses Episodic which
    // now skips recency under #97's stable-substrate rule.
    private static MemoryRecord MakeInteriorRecord(
        DateTimeOffset? confirmedAt,
        string?         confirmedBy,
        float           importance = 0.5f)
    {
        var (_, rec) = IdenticalUnitEmbeddings();
        return new MemoryRecord
        {
            Id          = Guid.NewGuid(),
            Type        = MemoryType.InnerThought,
            Content     = "test",
            Importance  = importance,
            DecayTier   = DecayTier.Standard,
            Provenance  = EpistemicTier.Interior,
            OccurredAt  = DateTimeOffset.UtcNow.AddHours(-1),
            ConfirmedAt = confirmedAt,
            ConfirmedBy = confirmedBy,
            Embedding   = rec,
        };
    }

    [Fact]
    public void ConfirmedRecord_ReceivesMultiplicativeBoostOverUnconfirmedTwin()
    {
        var composer = Composer();
        var (query, _) = IdenticalUnitEmbeddings();

        var confirmed   = MakeRecord(DateTimeOffset.UtcNow.AddDays(-30), "canonical");
        var unconfirmed = MakeRecord(confirmedAt: null, confirmedBy: null);

        var scoreConfirmed   = composer.ComputeRetrievalScore(query, confirmed,   includeRecency: true);
        var scoreUnconfirmed = composer.ComputeRetrievalScore(query, unconfirmed, includeRecency: true);

        scoreConfirmed.Should().BeApproximately(
            scoreUnconfirmed * (float)(1.0 + DefaultBoost), precision: 0.0001f,
            because: "confirmed records receive a (1 + boost) multiplicative bump on the base composite");
    }

    [Fact]
    public void ConfirmedEpisodic_BeatsUnconfirmedEpisodicWithHigherImportance()
    {
        // Confirmation-boost math: a canonical-confirmed record with
        // importance 0.3 outranks an unconfirmed record with importance 0.8
        // at the same cosine, because the multiplicative boost applied to
        // the confirmed record dominates the importance-only delta.
        //
        // PR #153 Devin review-fix (2026-08-27): renamed from
        // ConfirmedInterior_BeatsUnconfirmedInteriorWithHigherImportance —
        // the pre-existing name said "Interior" but MakeRecord defaults to
        // Provenance=Episodic. Post-#97 both records take the stable-
        // substrate recency=1.0 path, so this test now covers the Episodic
        // boost path. Interior-tier coverage lives in the sibling test
        // ConfirmedInterior_BeatsUnconfirmedInteriorWithHigherImportance
        // below, which uses MakeInteriorRecord explicitly.
        var composer = Composer();
        var (query, _) = IdenticalUnitEmbeddings();

        var laundered   = MakeRecord(confirmedAt: null, confirmedBy: null, importance: 0.8f);
        var realConfirmed = MakeRecord(
            confirmedAt: DateTimeOffset.UtcNow.AddDays(-30),
            confirmedBy: "canonical",
            importance: 0.3f);

        var scoreLaundered = composer.ComputeRetrievalScore(query, laundered,     includeRecency: true);
        var scoreReal      = composer.ComputeRetrievalScore(query, realConfirmed, includeRecency: true);

        scoreReal.Should().BeGreaterThan(scoreLaundered,
            because: "canonical-confirmed real Episodic content should outrank importance-inflated laundered Episodic content at the same cosine");
    }

    [Fact]
    public void ConfirmedInterior_BeatsUnconfirmedInteriorWithHigherImportance()
    {
        // PR #153 Devin review-fix (2026-08-27): added to restore honest
        // Interior-tier coverage of the confirmation-boost math after the
        // sibling test above became Episodic-only under the #97 stable-
        // substrate recency-off rule. Same math shape as the sibling but
        // both records are Interior tier, so the recency-decay branch is
        // exercised (not the recency=1.0 short-circuit).
        var composer = Composer();
        var (query, _) = IdenticalUnitEmbeddings();

        var laundered   = MakeInteriorRecord(confirmedAt: null, confirmedBy: null, importance: 0.8f);
        var realConfirmed = MakeInteriorRecord(
            confirmedAt: DateTimeOffset.UtcNow.AddDays(-30),
            confirmedBy: "canonical",
            importance: 0.3f);

        var scoreLaundered = composer.ComputeRetrievalScore(query, laundered,     includeRecency: true);
        var scoreReal      = composer.ComputeRetrievalScore(query, realConfirmed, includeRecency: true);

        scoreReal.Should().BeGreaterThan(scoreLaundered,
            because: "canonical-confirmed Interior content should outrank importance-inflated laundered Interior content at the same cosine, even under recency-decay");
    }

    [Fact]
    public void UnconfirmedInteriorRecord_NoBoost_MatchesLegacyFormula()
    {
        var composer = Composer();
        var (query, _) = IdenticalUnitEmbeddings();

        // Issue #97 (2026-08-27) — after the stable-substrate recency-off
        // change, only EpistemicTier.Interior retains the legacy recency-
        // decay formula. Interior is where "staleness IS a legitimate
        // signal" per the issue's Mark quote. Facts and Episodic records
        // now skip recency (see UnconfirmedEpisodicRecord_Issue97 below).
        var record = MakeInteriorRecord(confirmedAt: null, confirmedBy: null, importance: 0.5f);

        var actual = composer.ComputeRetrievalScore(query, record, includeRecency: true);

        // Legacy formula: 0.65 * cosine(1.0) + 0.10 * importance(0.5) + 0.25 * recency.
        // Recency: type=InnerThought → GetDecayMultiplier returns 1.0;
        // hoursSince ≈ 1; lambda = 48*1 = 48; recency = e^(-1/48) ≈ 0.9794.
        var expected = 0.65 * 1.0 + 0.10 * 0.5 + 0.25 * Math.Exp(-1.0 / 48.0);
        actual.Should().BeApproximately((float)expected, precision: 0.001f,
            because: "unconfirmed Interior records follow the pre-Issue-93 recency-decay formula unchanged");
    }

    [Fact]
    public void UnconfirmedEpisodicRecord_Issue97_SkipsRecency()
    {
        // Issue #97 (2026-08-27) — stable-substrate recency-off. Episodic
        // records are biographical / verbatim-conversational; time-passage
        // doesn't invalidate them, only Feature 30 canonical supersession
        // does. Recency is treated as 1.0 for scoring (same magnitude as
        // Anchored) so the α·cosine + β·importance + γ·1 blend keeps the
        // full-weight shape without decay penalty.
        var composer = Composer();
        var (query, _) = IdenticalUnitEmbeddings();

        // Default MakeRecord uses type=Episodic + Provenance=Episodic (the
        // MemoryRecord default). Age is ~1h; under legacy formula this
        // was recency ≈ 0.9896. Under the #97 fix it should be exactly 1.0.
        var youngRecord = MakeRecord(confirmedAt: null, confirmedBy: null, importance: 0.5f);
        var oldRecord   = new MemoryRecord
        {
            Id          = Guid.NewGuid(),
            Type        = MemoryType.Episodic,
            Content     = "test",
            Importance  = 0.5f,
            DecayTier   = DecayTier.Standard,
            Provenance  = EpistemicTier.Episodic,
            OccurredAt  = DateTimeOffset.UtcNow.AddDays(-90), // ~90d old
            Embedding   = IdenticalUnitEmbeddings().Item2,
        };

        var scoreYoung = composer.ComputeRetrievalScore(query, youngRecord, includeRecency: true);
        var scoreOld   = composer.ComputeRetrievalScore(query, oldRecord,   includeRecency: true);

        // Both should produce the same score post-fix: 0.65 * 1.0 + 0.10 * 0.5 + 0.25 * 1.0.
        var expected = 0.65 * 1.0 + 0.10 * 0.5 + 0.25 * 1.0;
        scoreYoung.Should().BeApproximately((float)expected, precision: 0.001f,
            because: "young Episodic records score with recency=1.0 (stable substrate) post-#97");
        scoreOld.Should().BeApproximately((float)expected, precision: 0.001f,
            because: "old Episodic records score identically to young ones post-#97 — time passage doesn't invalidate biographical content");
    }

    [Fact]
    public void ConfirmationBoost_Zero_DisablesBias()
    {
        // Operability: setting the boost to 0 in config restores the
        // pre-Issue-93 formula for every record. Emergency-off switch.
        var composer = Composer(boost: 0.0);
        var (query, _) = IdenticalUnitEmbeddings();

        var confirmed   = MakeRecord(DateTimeOffset.UtcNow, "canonical");
        var unconfirmed = MakeRecord(confirmedAt: null, confirmedBy: null);

        var scoreConfirmed   = composer.ComputeRetrievalScore(query, confirmed,   includeRecency: true);
        var scoreUnconfirmed = composer.ComputeRetrievalScore(query, unconfirmed, includeRecency: true);

        scoreConfirmed.Should().BeApproximately(scoreUnconfirmed, precision: 0.0001f,
            because: "boost=0 means confirmed and unconfirmed twins score identically");
    }

    [Fact]
    public void ConfirmationBoost_AppliesOnRecencyOffBranch_Too()
    {
        // Facts-tier retrieval uses includeRecency=false. The boost must apply
        // on that branch too — otherwise confirmed Facts get no bias, which is
        // the opposite of the design.
        var composer = Composer();
        var (query, _) = IdenticalUnitEmbeddings();

        var confirmed   = MakeRecord(DateTimeOffset.UtcNow, "canonical");
        var unconfirmed = MakeRecord(confirmedAt: null, confirmedBy: null);

        var scoreConfirmed   = composer.ComputeRetrievalScore(query, confirmed,   includeRecency: false);
        var scoreUnconfirmed = composer.ComputeRetrievalScore(query, unconfirmed, includeRecency: false);

        scoreConfirmed.Should().BeApproximately(
            scoreUnconfirmed * (float)(1.0 + DefaultBoost), precision: 0.0001f,
            because: "the recency-off branch (Facts tier) also gets the confirmation bump");
    }

    /// <summary>
    /// Factory stub whose CreateDbContext / CreateDbContextAsync throws on use —
    /// safety net so any test path that accidentally reaches the DB fails
    /// loud rather than silently returning an empty context.
    /// </summary>
    private sealed class StubDbContextFactory : IDbContextFactory<AniDbContext>
    {
        public AniDbContext CreateDbContext() =>
            throw new InvalidOperationException(
                "ComputeRetrievalScore must not touch the DB — factory stub called.");
    }
}
