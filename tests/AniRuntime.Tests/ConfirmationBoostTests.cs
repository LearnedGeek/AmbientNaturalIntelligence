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
    public void ConfirmedInterior_BeatsUnconfirmedInteriorWithHigherImportance()
    {
        // The empirical anchor: an Interior record with importance 0.8 (laundered
        // over months) beats an Episodic record with importance 0.3 (real
        // conversation) under the additive formula. Once the Episodic record is
        // marked confirmed via canonical backfill, it wins even at half the
        // importance.
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
            because: "canonical-confirmed real content should outrank importance-inflated laundered content at the same cosine");
    }

    [Fact]
    public void UnconfirmedRecord_NoBoost_MatchesLegacyFormula()
    {
        var composer = Composer();
        var (query, _) = IdenticalUnitEmbeddings();

        var record = MakeRecord(confirmedAt: null, confirmedBy: null, importance: 0.5f);

        var actual = composer.ComputeRetrievalScore(query, record, includeRecency: true);

        // Legacy formula: 0.65 * cosine(1.0) + 0.10 * importance(0.5) + 0.25 * recency.
        // Recency: type=Episodic → multiplier 2.0; hoursSince ≈ 1; lambda = 48*2 = 96.
        // recency = e^(-1/96) ≈ 0.9896.
        var expected = 0.65 * 1.0 + 0.10 * 0.5 + 0.25 * Math.Exp(-1.0 / 96.0);
        actual.Should().BeApproximately((float)expected, precision: 0.001f,
            because: "unconfirmed records follow the pre-Issue-93 formula unchanged");
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
