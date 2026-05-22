using AniRuntime.Core.Interfaces;
using AniRuntime.Dashboard.Statistics;

namespace AniRuntime.Dashboard.Endpoints;

/// <summary>
/// Issue #41 Path B (2026-05-21) — REST endpoints for the per-turn
/// expression-classification surface. Replaces the JSON-file scanning
/// path in Research.razor. Consumers (dashboard heatmap, paper-figure
/// tooling, future per-conversation mood-tracking panel) all read from
/// SQLite via this surface — single source of truth.
/// </summary>
public static class ExpressionClassificationEndpoints
{
    public static RouteGroupBuilder MapExpressionClassificationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/expressions").WithTags("Expressions");

        // GET /recent — most recent N classifications, optionally filtered by source.
        group.MapGet("/recent", async (
            IExpressionClassificationStore store,
            int limit = 100,
            string? source = null,
            CancellationToken ct = default) =>
        {
            var rows = await store.GetRecentAsync(limit, source, ct);
            return Results.Ok(rows.Select(r => new
            {
                r.Id,
                r.Source,
                r.ThreadId,
                r.MessageId,
                r.SourceFile,
                r.SourceIndex,
                r.Role,
                r.FeltRegister,
                r.ExpressedEmotion,
                r.Confidence,
                r.ClassifiedAt,
            }));
        });

        // GET /by-thread/{id} — per-conversation mood-tracking panel feed (#41 C.4).
        group.MapGet("/by-thread/{threadId:guid}", async (
            Guid threadId,
            IExpressionClassificationStore store,
            CancellationToken ct) =>
        {
            var rows = (await store.GetByThreadIdAsync(threadId, ct)).ToList();
            if (rows.Count == 0) return Results.NotFound();
            return Results.Ok(rows.Select(r => new
            {
                r.Role,
                r.FeltRegister,
                r.ExpressedEmotion,
                r.ClassifiedAt,
            }));
        });

        // GET /coupling — aggregate cross-tab + Cramér's V over felt × expressed.
        // For batch_dashboard source (which only writes ExpressedEmotion), the
        // matrix is over (user.ExpressedEmotion × response.ExpressedEmotion) —
        // pair-shape consistent with the pre-#41 dashboard heatmap. For runtime
        // source, the natural pairing is (felt_register × felt_register) at
        // turn-pair granularity (subject to producer evolution).
        group.MapGet("/coupling/{source}", async (
            string source,
            IExpressionClassificationStore store,
            CancellationToken ct) =>
        {
            // Pull a wide window (chunk 4/8 — paging refinement is a follow-on).
            var rows = (await store.GetRecentAsync(10000, source, ct)).ToList();
            if (rows.Count == 0)
                return Results.Ok(new
                {
                    Source       = source,
                    N            = 0,
                    Coupling     = new Dictionary<string, int>(),
                    Stats        = (CrosstabStatistics.CouplingStats?)null,
                });

            // Pair up consecutive (user, response) rows by SourceFile +
            // SourceIndex (batch) or by ThreadId + ClassifiedAt order
            // (runtime). For batch source the existing pre-#41 JSON shape was
            // "userEmotion|responseEmotion"; mirror it here.
            var coupling = new Dictionary<string, int>();
            if (source == "batch_dashboard")
            {
                var pairs = rows
                    .Where(r => r.SourceFile is not null && r.SourceIndex is not null && r.ExpressedEmotion is not null)
                    .GroupBy(r => (r.SourceFile, r.SourceIndex))
                    .Where(g => g.Count() == 2)
                    .Select(g =>
                    {
                        var u = g.FirstOrDefault(r => r.Role == "user");
                        var v = g.FirstOrDefault(r => r.Role == "response");
                        return (u, v);
                    })
                    .Where(p => p.u is not null && p.v is not null);

                foreach (var (u, v) in pairs)
                {
                    var key = $"{u!.ExpressedEmotion}|{v!.ExpressedEmotion}";
                    coupling[key] = coupling.GetValueOrDefault(key, 0) + 1;
                }
            }
            else
            {
                // Runtime: pair consecutive (mark, ani) by thread + sort order.
                foreach (var threadGroup in rows
                    .Where(r => r.ThreadId is not null && r.FeltRegister is not null)
                    .GroupBy(r => r.ThreadId!.Value))
                {
                    var ordered = threadGroup.OrderBy(r => r.ClassifiedAt).ToList();
                    for (int i = 0; i + 1 < ordered.Count; i++)
                    {
                        if (ordered[i].Role == "mark" && ordered[i + 1].Role == "ani")
                        {
                            var key = $"{ordered[i].FeltRegister}|{ordered[i + 1].FeltRegister}";
                            coupling[key] = coupling.GetValueOrDefault(key, 0) + 1;
                            i++; // consume the pair
                        }
                    }
                }
            }

            var rowLabels = coupling.Keys.Select(k => k.Split('|')[0]).Distinct().OrderBy(s => s).ToList();
            var colLabels = coupling.Keys.Select(k => k.Split('|')[1]).Distinct().OrderBy(s => s).ToList();
            var stats     = CrosstabStatistics.Compute(coupling, rowLabels, colLabels);

            return Results.Ok(new
            {
                Source       = source,
                N            = stats.N,
                Coupling     = coupling,
                RowLabels    = rowLabels,
                ColLabels    = colLabels,
                Stats        = stats,
            });
        });

        return group;
    }
}
