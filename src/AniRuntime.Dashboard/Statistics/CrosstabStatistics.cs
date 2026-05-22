namespace AniRuntime.Dashboard.Statistics;

/// <summary>
/// Issue #41 Path B (2026-05-21) — shared chi-squared / Cramér's V
/// computation over a `(row × column)` contingency table. Extracted from
/// Research.razor (ComputeStatistics, ChiSquaredSurvival) so the dashboard
/// page and the new <c>ExpressionClassificationEndpoints</c> share one
/// implementation. Single source of truth — fixes the V=0.302 vs
/// V=0.476 discrepancy by ensuring all consumers compute the statistic
/// the same way.
/// </summary>
public static class CrosstabStatistics
{
    public sealed record CouplingStats(
        double  CramersV,
        double  ChiSquared,
        int     DegreesOfFreedom,
        int     N,
        double  PValue,
        int     RowCount,
        int     ColCount);

    /// <summary>
    /// Compute chi-squared + Cramér's V from a contingency table.
    /// <paramref name="counts"/> is a flat dictionary keyed by
    /// <c>"row|col"</c>; same shape as Research.razor's <c>_couplingMatrix</c>.
    /// </summary>
    public static CouplingStats Compute(
        IReadOnlyDictionary<string, int> counts,
        IReadOnlyList<string>            rowLabels,
        IReadOnlyList<string>            colLabels)
    {
        int r = rowLabels.Count;
        int k = colLabels.Count;

        if (r < 2 || k < 2)
            return new CouplingStats(0, 0, 0, 0, 1, r, k);

        var observed = new int[r, k];
        int n = 0;
        for (int i = 0; i < r; i++)
            for (int j = 0; j < k; j++)
            {
                var key = $"{rowLabels[i]}|{colLabels[j]}";
                var cellCount = counts.TryGetValue(key, out var c) ? c : 0;
                observed[i, j] = cellCount;
                n += cellCount;
            }

        if (n == 0)
            return new CouplingStats(0, 0, 0, 0, 1, r, k);

        var rowTotals = new int[r];
        var colTotals = new int[k];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < k; j++)
            {
                rowTotals[i] += observed[i, j];
                colTotals[j] += observed[i, j];
            }

        double chi2 = 0;
        for (int i = 0; i < r; i++)
            for (int j = 0; j < k; j++)
            {
                double expected = (double)rowTotals[i] * colTotals[j] / n;
                if (expected > 0)
                    chi2 += Math.Pow(observed[i, j] - expected, 2) / expected;
            }

        int df     = (r - 1) * (k - 1);
        int minDim = Math.Min(r, k) - 1;
        double v   = minDim > 0 ? Math.Sqrt(chi2 / (n * minDim)) : 0;
        double p   = ChiSquaredSurvival(chi2, df);

        return new CouplingStats(v, chi2, df, n, p, r, k);
    }

    /// <summary>
    /// Approximate chi-squared survival function (upper tail). Mirrors
    /// the implementation in Research.razor pre-#41; ported here so the
    /// dashboard and endpoints share one source.
    /// </summary>
    public static double ChiSquaredSurvival(double x, int df)
    {
        if (x <= 0 || df <= 0) return 1.0;

        double k = df / 2.0;
        double xHalf = x / 2.0;

        // Wilson-Hilferty cube-root approximation — accurate for df ≥ 3.
        if (df >= 3)
        {
            double term = Math.Pow(xHalf / k, 1.0 / 3.0);
            double z    = (term - (1.0 - 1.0 / (9.0 * k))) / Math.Sqrt(1.0 / (9.0 * k));
            // Standard-normal survival
            return 0.5 * Erfc(z / Math.Sqrt(2.0));
        }

        // df = 1 or 2 — closed forms
        if (df == 1)
            return Erfc(Math.Sqrt(xHalf));
        // df == 2
        return Math.Exp(-xHalf);
    }

    /// <summary>Complementary error function (Abramowitz & Stegun 7.1.26).</summary>
    private static double Erfc(double x)
    {
        double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
        double poly = t * (0.254829592
                  + t * (-0.284496736
                  + t * (1.421413741
                  + t * (-1.453152027
                  + t * 1.061405429))));
        double e = poly * Math.Exp(-x * x);
        return x >= 0 ? e : 2.0 - e;
    }
}
