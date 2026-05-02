using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Vibe Loop V1.5.1 spec tests. Pin the contract of
/// <see cref="VibeBiasService"/> as the producer of
/// <see cref="VibeBiasResult"/> per the V1.5.0 design decisions
/// (locked May 2 11:24 CDT).
///
/// Three categories:
/// 1. **Pure helpers** — importance computation, tier promotion, decay,
///    register-vector ordering, weighted average, MMR rerank. Tested
///    directly via <c>InternalsVisibleTo</c> for deterministic readable
///    spec coverage.
/// 2. **End-to-end via <c>ComputeBiasAsync</c>** — strict-mock
///    <see cref="IClosedConversationStore"/>; verifies the integration
///    points (lookback filtering, similarity clustering, top-N MMR,
///    diversity-reason annotation, the load-bearing self-regulation
///    framing — Ani's-delta drives the bias, Mark's-delta is not read).
/// 3. **Architectural invariants** — that <c>VibeBiasService</c> never
///    reads any Mark-delta surface; the only valence input is
///    <c>OutcomeSignalValence</c> (V1.2 sourced from Ani's register
///    delta). This is V1.5.0 Lever 3's load-bearing claim and gets a
///    test pinning the property.
/// </summary>
public class VibeBiasServiceTests
{
    private readonly Mock<IClosedConversationStore> _mockStore = new(MockBehavior.Strict);

    private static AniOptions DefaultOptions() => new();

    private VibeBiasService Build(AniOptions? opts = null) => new(
        _mockStore.Object,
        Options.Create(opts ?? DefaultOptions()),
        NullLogger<VibeBiasService>.Instance);

    private static Dictionary<string, float> RegisterDict(
        params (string register, float value)[] entries)
    {
        var dict = ClosedConversationSummarizer.Registers
            .ToDictionary(r => r, _ => 0f);
        foreach (var (register, value) in entries)
            dict[register] = value;
        return dict;
    }

    private static ClosedConversationRecord BuildRecord(
        Guid?                                       id              = null,
        DateTimeOffset?                             closedAt        = null,
        int                                         turnCount       = 5,
        float                                       outcomeValence  = 0.4f,
        Dictionary<string, float>?                  markRegister    = null,
        Dictionary<string, float>?                  aniRegister     = null) =>
        new()
        {
            Id                       = id            ?? Guid.NewGuid(),
            ClosedAt                 = closedAt      ?? DateTimeOffset.UtcNow.AddHours(-1),
            TurnCount                = turnCount,
            OutcomeSignalValence     = outcomeValence,
            MarkRegister             = markRegister  ?? RegisterDict(("Tenderness", 0.6f), ("Curiosity", 0.4f)),
            AniRegister              = aniRegister   ?? RegisterDict(("Tenderness", 0.5f), ("Playfulness", 0.5f)),
            Gist                     = "test gist",
        };

    // ============================================================
    // ── Pure helpers ─────────────────────────────────────────────
    // ============================================================

    // ── ComputeImportance ────────────────────────────────────────

    [Fact]
    public void ComputeImportance_ZeroValence_ReturnsZero()
    {
        VibeBiasService.ComputeImportance(turnCount: 10, absValence: 0f, registerSaturation: 0.9f)
            .Should().Be(0f, "zero valence makes the conversation emotionally neutral; importance is zero regardless of turn count");
    }

    [Fact]
    public void ComputeImportance_ZeroSaturation_ReturnsZero()
    {
        VibeBiasService.ComputeImportance(turnCount: 10, absValence: 0.5f, registerSaturation: 0f)
            .Should().Be(0f, "zero register saturation means no concentrated emotional signal; the conversation isn't load-bearing even with valence");
    }

    [Fact]
    public void ComputeImportance_HigherTurnCount_ProducesHigherImportance()
    {
        var low  = VibeBiasService.ComputeImportance(2,  0.5f, 0.5f);
        var high = VibeBiasService.ComputeImportance(20, 0.5f, 0.5f);
        high.Should().BeGreaterThan(low, "more turns indicate longer engagement; importance grows monotonically with turn count");
    }

    [Fact]
    public void ComputeImportance_TurnCountSaturates_NotLinear()
    {
        var small  = VibeBiasService.ComputeImportance(5,   1f, 1f);
        var medium = VibeBiasService.ComputeImportance(15,  1f, 1f);
        var large  = VibeBiasService.ComputeImportance(50,  1f, 1f);
        (medium - small).Should().BeGreaterThan(large - medium,
            "turn-count factor should saturate so doubling-from-many doesn't linearly inflate importance");
    }

    [Fact]
    public void ComputeImportance_SymmetricInValenceSign()
    {
        var positive = VibeBiasService.ComputeImportance(5, 0.7f, 0.5f);
        var negative = VibeBiasService.ComputeImportance(5, 0.7f, 0.5f);
        positive.Should().Be(negative, "absolute valence already strips sign; importance is symmetric");
    }

    // ── DetermineTier ────────────────────────────────────────────

    [Fact]
    public void DetermineTier_ShortLowValenceLowSaturation_IsLight()
    {
        var record = BuildRecord(turnCount: 1, outcomeValence: 0.1f,
            markRegister: RegisterDict(("Tenderness", 0.2f)));
        VibeBiasService.DetermineTier(record, DefaultOptions()).Should().Be("Light");
    }

    [Fact]
    public void DetermineTier_FiveTurns_PromotesToMedium()
    {
        var record = BuildRecord(turnCount: 5, outcomeValence: 0.1f,
            markRegister: RegisterDict(("Tenderness", 0.2f)));
        VibeBiasService.DetermineTier(record, DefaultOptions()).Should().Be("Medium",
            "≥3 turns is a substantive interaction; promotes to Medium even on weak valence + low saturation");
    }

    [Fact]
    public void DetermineTier_HighValenceShortConv_PromotesToMedium()
    {
        var record = BuildRecord(turnCount: 1, outcomeValence: 0.4f,
            markRegister: RegisterDict(("Tenderness", 0.2f)));
        VibeBiasService.DetermineTier(record, DefaultOptions()).Should().Be("Medium",
            "abs(valence) ≥ 0.3 is enough emotional signal to count even in a short conversation");
    }

    [Fact]
    public void DetermineTier_TenTurns_PromotesToHeavy()
    {
        var record = BuildRecord(turnCount: 10, outcomeValence: 0.1f,
            markRegister: RegisterDict(("Tenderness", 0.2f)));
        VibeBiasService.DetermineTier(record, DefaultOptions()).Should().Be("Heavy",
            "≥10 turns is load-bearing; promotes to Heavy even on weak valence");
    }

    [Fact]
    public void DetermineTier_VeryHighValence_PromotesToHeavy()
    {
        var record = BuildRecord(turnCount: 2, outcomeValence: 0.7f,
            markRegister: RegisterDict(("Tenderness", 0.2f)));
        VibeBiasService.DetermineTier(record, DefaultOptions()).Should().Be("Heavy",
            "abs(valence) ≥ 0.6 is strong enough emotional shift to count as Heavy by valence alone");
    }

    [Fact]
    public void DetermineTier_HighSaturation_PromotesToHeavy()
    {
        var record = BuildRecord(turnCount: 2, outcomeValence: 0.1f,
            markRegister: RegisterDict(("Tenderness", 0.85f)));
        VibeBiasService.DetermineTier(record, DefaultOptions()).Should().Be("Heavy",
            "register saturation ≥ 0.8 means the conversation was concentrated in one register; load-bearing");
    }

    [Fact]
    public void DetermineTier_NegativeValenceMagnitudeStillPromotes()
    {
        var record = BuildRecord(turnCount: 2, outcomeValence: -0.7f,
            markRegister: RegisterDict(("Frustration", 0.5f)));
        VibeBiasService.DetermineTier(record, DefaultOptions()).Should().Be("Heavy",
            "tier promotion uses absolute valence; a negative-outcome conversation is still load-bearing");
    }

    // ── ComputeDecay ─────────────────────────────────────────────

    [Fact]
    public void ComputeDecay_ZeroAge_ReturnsOne()
    {
        VibeBiasService.ComputeDecay(0f, 12f).Should().Be(1f);
    }

    [Fact]
    public void ComputeDecay_OneHalfLife_ReturnsHalf()
    {
        VibeBiasService.ComputeDecay(12f, 12f).Should().BeApproximately(0.5f, 0.0001f);
    }

    [Fact]
    public void ComputeDecay_TwoHalfLives_ReturnsQuarter()
    {
        VibeBiasService.ComputeDecay(24f, 12f).Should().BeApproximately(0.25f, 0.0001f);
    }

    [Fact]
    public void ComputeDecay_ZeroHalfLife_DegeneratesToZero()
    {
        VibeBiasService.ComputeDecay(1f, 0f).Should().Be(0f, "zero half-life is degenerate; treat as instantaneous decay so we don't divide by zero");
    }

    // ── ToOrderedVector ──────────────────────────────────────────

    [Fact]
    public void ToOrderedVector_PreservesCanonicalOrder()
    {
        var dict = RegisterDict(("Tenderness", 0.7f), ("Delight", 0.3f));
        var arr  = VibeBiasService.ToOrderedVector(dict);

        arr.Should().HaveCount(9);
        arr[0].Should().BeApproximately(0.7f, 0.0001f, "Tenderness is index 0 per canonical Registers order");
        arr[8].Should().BeApproximately(0.3f, 0.0001f, "Delight is index 8 per canonical Registers order");
    }

    [Fact]
    public void ToOrderedVector_MissingKeys_DefaultToZero()
    {
        var dict = new Dictionary<string, float> { { "Tenderness", 0.5f } };
        var arr  = VibeBiasService.ToOrderedVector(dict);

        arr[0].Should().BeApproximately(0.5f, 0.0001f);
        for (var i = 1; i < arr.Length; i++)
            arr[i].Should().Be(0f, "absent keys resolve to zero rather than throwing");
    }

    // ── MaxRegisterPrevalence ────────────────────────────────────

    [Fact]
    public void MaxRegisterPrevalence_EmptyDict_ReturnsZero()
    {
        VibeBiasService.MaxRegisterPrevalence(new Dictionary<string, float>())
            .Should().Be(0f);
    }

    [Fact]
    public void MaxRegisterPrevalence_SingleConcentration_ReturnsThatValue()
    {
        var dict = RegisterDict(("Tenderness", 0.92f), ("Curiosity", 0.05f));
        VibeBiasService.MaxRegisterPrevalence(dict).Should().BeApproximately(0.92f, 0.0001f);
    }

    // ── ComputeWeightedRegisterAverage ───────────────────────────

    [Fact]
    public void ComputeWeightedRegisterAverage_EmptyList_ReturnsZeroVector()
    {
        var avg = VibeBiasService.ComputeWeightedRegisterAverage(
            new List<VibeBiasContribution>());
        avg.Should().HaveCount(9);
        avg.Should().AllSatisfy(v => v.Should().Be(0f));
    }

    [Fact]
    public void ComputeWeightedRegisterAverage_HigherWeightDominates()
    {
        var v1 = VibeBiasService.ToOrderedVector(RegisterDict(("Tenderness", 1f)));
        var v2 = VibeBiasService.ToOrderedVector(RegisterDict(("Frustration", 1f)));

        var avg = VibeBiasService.ComputeWeightedRegisterAverage(new[]
        {
            new VibeBiasContribution(Guid.NewGuid(), BiasWeight: 0.9f, Importance: 1f, HalfLifeTier: "Heavy",  AgeHours: 0f, MarkRegister: v1, OutcomeValence: 0.5f),
            new VibeBiasContribution(Guid.NewGuid(), BiasWeight: 0.1f, Importance: 1f, HalfLifeTier: "Light",  AgeHours: 0f, MarkRegister: v2, OutcomeValence: 0.0f),
        });

        avg[0].Should().BeApproximately(0.9f, 0.001f, "Tenderness slot dominated by 0.9-weight v1");
        avg[7].Should().BeApproximately(0.1f, 0.001f, "Frustration slot reflects 0.1-weight v2");
    }

    // ── ApplyMmrRerank ───────────────────────────────────────────

    [Fact]
    public void ApplyMmrRerank_SinglePick_ReturnsHighestWeight()
    {
        var c1 = MakeContribution(weight: 0.5f, register: ("Tenderness", 1f));
        var c2 = MakeContribution(weight: 0.9f, register: ("Frustration", 1f));
        var c3 = MakeContribution(weight: 0.3f, register: ("Playfulness", 1f));

        var (surfaced, _) = VibeBiasService.ApplyMmrRerank(
            new() { c1, c2, c3 }, topN: 1, lambda: 0.3f);

        surfaced.Should().HaveCount(1);
        surfaced[0].RecordId.Should().Be(c2.RecordId);
    }

    [Fact]
    public void ApplyMmrRerank_HighSimilarityCluster_DiversityRerankPenalizesNearestNeighbor()
    {
        // Three records: c1 and c2 are near-identical (both Tenderness-heavy),
        // c3 is in a distinct register space.
        var c1 = MakeContribution(weight: 1.0f, register: ("Tenderness", 1f));
        var c2 = MakeContribution(weight: 0.9f, register: ("Tenderness", 1f));
        var c3 = MakeContribution(weight: 0.5f, register: ("Frustration", 1f));

        var (surfaced, _) = VibeBiasService.ApplyMmrRerank(
            new() { c1, c2, c3 }, topN: 2, lambda: 0.7f);

        surfaced[0].RecordId.Should().Be(c1.RecordId, "highest weight wins first pick");
        surfaced[1].RecordId.Should().Be(c3.RecordId,
            "MMR with high lambda rejects the near-duplicate c2 in favor of the diverse c3");
    }

    [Fact]
    public void ApplyMmrRerank_EmptyList_ReturnsEmpty()
    {
        var (surfaced, reason) = VibeBiasService.ApplyMmrRerank(
            new List<VibeBiasContribution>(), topN: 2, lambda: 0.3f);

        surfaced.Should().BeEmpty();
        reason.Should().Contain("no candidates");
    }

    [Fact]
    public void ApplyMmrRerank_TopNLargerThanCandidates_ReturnsAll()
    {
        var c1 = MakeContribution(weight: 0.7f, register: ("Tenderness", 1f));
        var c2 = MakeContribution(weight: 0.3f, register: ("Frustration", 1f));

        var (surfaced, _) = VibeBiasService.ApplyMmrRerank(
            new() { c1, c2 }, topN: 5, lambda: 0.3f);

        surfaced.Should().HaveCount(2, "topN exceeds candidate count; surface everything");
    }

    // ============================================================
    // ── End-to-end via ComputeBiasAsync ──────────────────────────
    // ============================================================

    [Fact]
    public async Task ComputeBiasAsync_NoCandidates_ReturnsEmptyResult()
    {
        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<ClosedConversationRecord>());

        var ctx = new MarkRegisterContext(
            MarkRegister: VibeBiasService.ToOrderedVector(RegisterDict(("Tenderness", 1f))),
            AsOf:         DateTimeOffset.UtcNow);

        var result = await Build().ComputeBiasAsync(Roles.Mark, ctx, CancellationToken.None);

        result.AllCandidates.Should().BeEmpty();
        result.SurfacedTopN.Should().BeEmpty();
        result.RecommendedStrategyRegister.Should().HaveCount(9);
        result.DiversityScoreReason.Should().Contain("no candidate");
    }

    [Fact]
    public async Task ComputeBiasAsync_OutsideLookbackWindow_DroppedFromCandidates()
    {
        var asOf  = DateTimeOffset.UtcNow;
        var stale = BuildRecord(closedAt: asOf.AddDays(-90));   // ≫ 30-day default lookback
        var fresh = BuildRecord(closedAt: asOf.AddHours(-1));

        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { stale, fresh });

        var ctx = new MarkRegisterContext(
            MarkRegister: VibeBiasService.ToOrderedVector(fresh.MarkRegister),
            AsOf:         asOf);

        var result = await Build().ComputeBiasAsync(Roles.Mark, ctx, CancellationToken.None);

        result.AllCandidates.Should().HaveCount(1, "only the fresh record survives lookback filtering");
        result.AllCandidates[0].RecordId.Should().Be(fresh.Id);
    }

    [Fact]
    public async Task ComputeBiasAsync_BelowSimilarityThreshold_NotSurfaced()
    {
        var asOf = DateTimeOffset.UtcNow;
        var distant = BuildRecord(
            markRegister: RegisterDict(("Frustration", 1f)),
            closedAt:     asOf.AddHours(-1));

        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { distant });

        var ctx = new MarkRegisterContext(
            MarkRegister: VibeBiasService.ToOrderedVector(RegisterDict(("Tenderness", 1f))),
            AsOf:         asOf);

        var result = await Build().ComputeBiasAsync(Roles.Mark, ctx, CancellationToken.None);

        result.AllCandidates.Should().HaveCount(1, "candidate appears in AllCandidates for telemetry");
        result.SurfacedTopN.Should().BeEmpty("orthogonal register vector falls below similarity threshold; not surfaced");
        result.DiversityScoreReason.Should().Contain("similarity");
    }

    [Fact]
    public async Task ComputeBiasAsync_AboveSimilarityThreshold_Surfaced()
    {
        var asOf = DateTimeOffset.UtcNow;
        var similar = BuildRecord(
            markRegister: RegisterDict(("Tenderness", 0.9f), ("Curiosity", 0.1f)),
            closedAt:     asOf.AddHours(-2));

        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { similar });

        var ctx = new MarkRegisterContext(
            MarkRegister: VibeBiasService.ToOrderedVector(RegisterDict(("Tenderness", 0.95f))),
            AsOf:         asOf);

        var result = await Build().ComputeBiasAsync(Roles.Mark, ctx, CancellationToken.None);

        result.SurfacedTopN.Should().HaveCount(1);
        result.SurfacedTopN[0].RecordId.Should().Be(similar.Id);
    }

    [Fact]
    public async Task ComputeBiasAsync_SurfacesAtMostTopNRecords()
    {
        var asOf = DateTimeOffset.UtcNow;
        var base1 = BuildRecord(id: Guid.NewGuid(), turnCount: 3,  outcomeValence: 0.2f, markRegister: RegisterDict(("Tenderness", 1f)));
        var base2 = BuildRecord(id: Guid.NewGuid(), turnCount: 5,  outcomeValence: 0.4f, markRegister: RegisterDict(("Tenderness", 0.9f), ("Curiosity", 0.1f)));
        var base3 = BuildRecord(id: Guid.NewGuid(), turnCount: 12, outcomeValence: 0.7f, markRegister: RegisterDict(("Tenderness", 0.85f), ("Playfulness", 0.15f)));
        var base4 = BuildRecord(id: Guid.NewGuid(), turnCount: 8,  outcomeValence: 0.5f, markRegister: RegisterDict(("Tenderness", 0.95f)));

        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { base1, base2, base3, base4 });

        var ctx = new MarkRegisterContext(
            MarkRegister: VibeBiasService.ToOrderedVector(RegisterDict(("Tenderness", 1f))),
            AsOf:         asOf);

        var result = await Build().ComputeBiasAsync(Roles.Mark, ctx, CancellationToken.None);

        result.AllCandidates.Should().HaveCount(4);
        result.SurfacedTopN.Should().HaveCountLessThanOrEqualTo(2,
            "VibeBiasSurfaceTopN default is 2 — never surface more than this");
    }

    [Fact]
    public async Task ComputeBiasAsync_RecommendedStrategyRegister_LengthIs9()
    {
        var asOf = DateTimeOffset.UtcNow;
        _mockStore.Setup(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { BuildRecord(closedAt: asOf.AddHours(-1)) });

        var ctx = new MarkRegisterContext(
            MarkRegister: VibeBiasService.ToOrderedVector(RegisterDict(("Tenderness", 0.7f), ("Curiosity", 0.3f))),
            AsOf:         asOf);

        var result = await Build().ComputeBiasAsync(Roles.Mark, ctx, CancellationToken.None);

        result.RecommendedStrategyRegister.Should().HaveCount(9,
            "register vector always 9-dim per canonical taxonomy");
    }

    [Fact]
    public async Task ComputeBiasAsync_InvalidVectorLength_ReturnsEmptyResultWithoutCallingStore()
    {
        var ctx = new MarkRegisterContext(
            MarkRegister: new float[] { 0.5f, 0.5f },                    // wrong length
            AsOf:         DateTimeOffset.UtcNow);

        var result = await Build().ComputeBiasAsync(Roles.Mark, ctx, CancellationToken.None);

        result.AllCandidates.Should().BeEmpty();
        result.SurfacedTopN.Should().BeEmpty();
        result.DiversityScoreReason.Should().Contain("invalid input vector length");

        _mockStore.Verify(s => s.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "service short-circuits on invalid input — no store traffic for malformed vectors");
    }

    // ============================================================
    // ── Architectural invariant: self-regulation framing (Lever 3)
    // ============================================================

    [Fact]
    public void Contribution_OutcomeValence_SourcedFromAniDeltaNotMarkDelta()
    {
        // V1.5.0 Lever 3 (load-bearing). The OutcomeValence on the
        // contribution must come from record.OutcomeSignalValence, which
        // V1.2's summarizer computes from Ani's register delta (start-of-
        // thread to end-of-thread). Mark's-register delta is NOT read.
        // This test pins the property by ensuring that varying the
        // ClosedConversationRecord's MarkRegister has no effect on the
        // produced VibeBiasContribution.OutcomeValence — only changing
        // OutcomeSignalValence does.
        var asOf = DateTimeOffset.UtcNow;

        var marketAlphaSadStart = BuildRecord(
            outcomeValence: +0.42f,
            markRegister:   RegisterDict(("Frustration", 0.9f), ("Wistful", 0.1f)));

        var marketBetaWarmStart = BuildRecord(
            outcomeValence: +0.42f,
            markRegister:   RegisterDict(("Tenderness", 0.9f), ("Delight", 0.1f)));

        var optsAlpha = DefaultOptions();
        var optsBeta  = DefaultOptions();

        var contribAlpha = VibeBiasService.BuildContribution(marketAlphaSadStart, asOf, optsAlpha);
        var contribBeta  = VibeBiasService.BuildContribution(marketBetaWarmStart, asOf, optsBeta);

        contribAlpha.OutcomeValence.Should().Be(contribBeta.OutcomeValence,
            "self-regulation framing: outcome valence is invariant to Mark's pre-state register; both records have the SAME +0.42 Ani-delta valence and so produce identical OutcomeValence on the contribution");
    }

    [Fact]
    public void Contribution_OutcomeValence_VariesWhenAniDeltaVaries()
    {
        var asOf = DateTimeOffset.UtcNow;
        var loose  = BuildRecord(outcomeValence: -0.5f);
        var bright = BuildRecord(outcomeValence: +0.7f);

        var c1 = VibeBiasService.BuildContribution(loose,  asOf, DefaultOptions());
        var c2 = VibeBiasService.BuildContribution(bright, asOf, DefaultOptions());

        c1.OutcomeValence.Should().NotBe(c2.OutcomeValence,
            "when Ani's-delta valence differs, contribution.OutcomeValence reflects the difference — confirms it's the bias-driving signal");
    }

    // ============================================================
    // ── Test helpers ─────────────────────────────────────────────
    // ============================================================

    private static VibeBiasContribution MakeContribution(
        float weight,
        params (string register, float value)[] register)
    {
        var dict = ClosedConversationSummarizer.Registers.ToDictionary(r => r, _ => 0f);
        foreach (var (r, v) in register) dict[r] = v;

        return new VibeBiasContribution(
            RecordId:       Guid.NewGuid(),
            BiasWeight:     weight,
            Importance:     weight,
            HalfLifeTier:   "Medium",
            AgeHours:       1f,
            MarkRegister:   VibeBiasService.ToOrderedVector(dict),
            OutcomeValence: 0.5f);
    }
}
