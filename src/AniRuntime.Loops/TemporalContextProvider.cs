using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// F-4 Phase 5a (2026-10-24) — default <see cref="ITemporalContextProvider"/>
/// implementation. Backed by <see cref="IMemorySearch.GetByTypeAsync"/>
/// against <see cref="MemoryType.InnerThought"/>, which already orders
/// descending by <see cref="MemoryRecord.OccurredAt"/>. One DB read per
/// cycle, no additional writes. See issue #184.
/// </summary>
public sealed class TemporalContextProvider : ITemporalContextProvider
{
    private readonly IMemorySearch                     _search;
    private readonly ILogger<TemporalContextProvider>  _log;

    public TemporalContextProvider(
        IMemorySearch                     search,
        ILogger<TemporalContextProvider>  log)
    {
        _search = search;
        _log    = log;
    }

    public async Task<TemporalContext> BuildAsync(
        DateTimeOffset    now,
        int               takeK,
        CancellationToken ct = default)
    {
        if (takeK <= 0)
            return new TemporalContext(null, Array.Empty<RecentInnerThought>());

        IReadOnlyList<RecentInnerThought> recent;
        double? hoursSince;

        try
        {
            var records = (await _search.GetByTypeAsync(
                    MemoryType.InnerThought, takeK, ct).ConfigureAwait(false))
                .ToList();

            recent = records
                .Where(r => !string.IsNullOrWhiteSpace(r.Content))
                .Select(r => new RecentInnerThought(r.Content, r.OccurredAt))
                .ToList();

            // IMemorySearch.GetByTypeAsync returns records ordered descending
            // by OccurredAt — the first non-empty entry is the latest inner
            // thought. Compute elapsed hours against `now` so we hand a
            // consistent clock through to the composer (snapshot.BuiltAt is
            // the authoritative "now" for the cycle).
            if (recent.Count > 0)
            {
                var elapsed = now - recent[0].OccurredAt;
                hoursSince  = Math.Max(0.0, elapsed.TotalHours);
            }
            else
            {
                hoursSince = null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // Fail-open per the interface contract — a Phase 5a stall must
            // never block the cognitive cycle. Composer sees null + empty
            // and skips the temporal block.
            // InvalidOperationException covers EF Core state faults
            // (connection closed, context disposed, concurrent access).
            _log.LogWarning(ex,
                "F-4 Phase 5a temporal context build failed; proceeding without temporal grounding.");
            return new TemporalContext(null, Array.Empty<RecentInnerThought>());
        }
        catch (SystemException ex)
        {
            // SystemException covers DbException/SqliteException and other
            // runtime faults from the SQLite path without swallowing
            // application-level exceptions derived directly from Exception.
            _log.LogWarning(ex,
                "F-4 Phase 5a temporal context build failed; proceeding without temporal grounding.");
            return new TemporalContext(null, Array.Empty<RecentInnerThought>());
        }

        return new TemporalContext(hoursSince, recent);
    }
}
