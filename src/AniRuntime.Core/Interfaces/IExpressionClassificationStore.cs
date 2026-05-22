using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Issue #41 (2026-05-21) — write/read surface for
/// <see cref="ExpressionClassificationRecord"/> instances. Replaces the
/// pre-#41 JSON file-system persistence in <c>Research.razor</c> +
/// the ad-hoc aggregate cross-tab dictionary with a per-row SQLite store
/// usable by both the dashboard heatmap and paper-figure drill-down.
///
/// Narrow interface per the ISP discipline. Ships Save + GetRecent +
/// GetByThread + a Cramér's-V-ready aggregation read; per-cell drill-down
/// reads land on dedicated methods when consumers actually need them.
/// </summary>
public interface IExpressionClassificationStore
{
    /// <summary>
    /// Persist one classification. Idempotent on
    /// (<see cref="ExpressionClassificationRecord.Source"/>,
    /// <see cref="ExpressionClassificationRecord.ContentHash"/>) — re-saving
    /// the same content + source is a no-op, so the batch dashboard can
    /// re-run its corpus without doubling counts.
    /// </summary>
    Task SaveAsync(ExpressionClassificationRecord record, CancellationToken ct = default);

    /// <summary>
    /// Most recent N records, ordered by <see cref="ExpressionClassificationRecord.ClassifiedAt"/>
    /// descending. Filter by source when needed; null returns both runtime
    /// and batch.
    /// </summary>
    Task<IEnumerable<ExpressionClassificationRecord>> GetRecentAsync(
        int limit = 100, string? source = null, CancellationToken ct = default);

    /// <summary>
    /// All runtime-source records for a given thread, ordered by
    /// <see cref="ExpressionClassificationRecord.ClassifiedAt"/> ascending.
    /// Powers the per-conversation mood-tracking panel (Issue #41 audit §C.4).
    /// </summary>
    Task<IEnumerable<ExpressionClassificationRecord>> GetByThreadIdAsync(
        Guid threadId, CancellationToken ct = default);
}
