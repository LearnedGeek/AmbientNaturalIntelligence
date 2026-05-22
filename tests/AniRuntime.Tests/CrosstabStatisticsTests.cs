using AniRuntime.Dashboard.Statistics;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #41 Path B spec tests for the shared chi-squared / Cramér's V
/// computation. The motivation for hoisting this math out of Research.razor
/// was to fix the V=0.302 vs V=0.476 dashboard/LinkedIn discrepancy — so
/// the tests pin the exact formula against textbook fixtures.
/// </summary>
public class CrosstabStatisticsTests
{
    /// <summary>
    /// SPEC: perfect dependence (identity matrix) produces V = 1.
    /// </summary>
    [Fact]
    public void Compute_PerfectDependence_ProducesV1()
    {
        var rows = new[] { "A", "B", "C" };
        var cols = new[] { "X", "Y", "Z" };
        var counts = new Dictionary<string, int>
        {
            { "A|X", 100 }, { "B|Y", 100 }, { "C|Z", 100 },
        };

        var stats = CrosstabStatistics.Compute(counts, rows, cols);

        stats.N.Should().Be(300);
        stats.CramersV.Should().BeApproximately(1.0, 0.001,
            "perfect dependence — every row maps to exactly one column");
        stats.DegreesOfFreedom.Should().Be(4);
    }

    /// <summary>
    /// SPEC: perfect independence (uniform table) produces V = 0.
    /// </summary>
    [Fact]
    public void Compute_PerfectIndependence_ProducesV0()
    {
        var rows = new[] { "A", "B" };
        var cols = new[] { "X", "Y" };
        var counts = new Dictionary<string, int>
        {
            { "A|X", 50 }, { "A|Y", 50 },
            { "B|X", 50 }, { "B|Y", 50 },
        };

        var stats = CrosstabStatistics.Compute(counts, rows, cols);

        stats.N.Should().Be(200);
        stats.CramersV.Should().BeApproximately(0.0, 0.001,
            "uniform marginals everywhere → expected matches observed → chi² = 0");
        stats.ChiSquared.Should().BeApproximately(0.0, 0.001);
    }

    /// <summary>
    /// SPEC: textbook 2x2 — chi² = 9.0 on the classic "20/80, 80/20" table
    /// with n=200 maps to V = sqrt(9/200) ≈ 0.212.
    /// </summary>
    [Fact]
    public void Compute_Textbook2x2_MatchesExpected()
    {
        var rows = new[] { "R1", "R2" };
        var cols = new[] { "C1", "C2" };
        var counts = new Dictionary<string, int>
        {
            { "R1|C1", 60 }, { "R1|C2", 40 },
            { "R2|C1", 40 }, { "R2|C2", 60 },
        };

        var stats = CrosstabStatistics.Compute(counts, rows, cols);

        // expected = (rowTotal * colTotal) / n = 50 for every cell
        // chi² = 4 * (10²/50) = 4 * 2 = 8
        // V = sqrt(8/200) ≈ 0.200
        stats.ChiSquared.Should().BeApproximately(8.0, 0.001);
        stats.CramersV.Should().BeApproximately(0.2, 0.001);
        stats.DegreesOfFreedom.Should().Be(1);
        stats.N.Should().Be(200);
    }

    /// <summary>
    /// SPEC: missing cells default to 0 — table edges aren't fragile to
    /// sparse data.
    /// </summary>
    [Fact]
    public void Compute_MissingCells_TreatedAsZero()
    {
        var rows = new[] { "A", "B" };
        var cols = new[] { "X", "Y", "Z" };
        var counts = new Dictionary<string, int>
        {
            { "A|X", 30 }, { "B|Z", 30 },
            // A|Y, A|Z, B|X, B|Y all missing → treated as 0
        };

        var stats = CrosstabStatistics.Compute(counts, rows, cols);

        stats.N.Should().Be(60);
        stats.CramersV.Should().BeGreaterThan(0.9, "near-perfect dependence");
    }

    /// <summary>
    /// SPEC: degenerate cases return zero rather than NaN/divide-by-zero.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Compute_DegenerateDimensions_ReturnsZero(int dim)
    {
        var rows = Enumerable.Range(0, dim).Select(i => $"R{i}").ToArray();
        var cols = new[] { "X", "Y" };
        var counts = new Dictionary<string, int>();

        var stats = CrosstabStatistics.Compute(counts, rows, cols);

        stats.CramersV.Should().Be(0);
        stats.ChiSquared.Should().Be(0);
    }

    /// <summary>
    /// SPEC: empty contingency (no observations) returns zero, p=1.
    /// </summary>
    [Fact]
    public void Compute_NoObservations_ReturnsZero()
    {
        var rows = new[] { "A", "B" };
        var cols = new[] { "X", "Y" };
        var counts = new Dictionary<string, int>();

        var stats = CrosstabStatistics.Compute(counts, rows, cols);

        stats.N.Should().Be(0);
        stats.CramersV.Should().Be(0);
        stats.PValue.Should().Be(1);
    }

    /// <summary>
    /// SPEC: ChiSquaredSurvival is well-behaved at boundaries.
    /// </summary>
    [Theory]
    [InlineData(0.0,  1, 1.0)]   // x=0 → full mass remaining
    [InlineData(-1.0, 1, 1.0)]   // invalid x → 1
    [InlineData(5.0,  0, 1.0)]   // invalid df → 1
    public void ChiSquaredSurvival_DegenerateInputs_ReturnsOne(double x, int df, double expected)
    {
        var p = CrosstabStatistics.ChiSquaredSurvival(x, df);
        p.Should().BeApproximately(expected, 0.001);
    }

    /// <summary>
    /// SPEC: large chi² with reasonable df → very small p.
    /// </summary>
    [Fact]
    public void ChiSquaredSurvival_LargeChiSquared_ProducesSmallP()
    {
        var p = CrosstabStatistics.ChiSquaredSurvival(50.0, 4);
        p.Should().BeLessThan(0.001);
    }
}
