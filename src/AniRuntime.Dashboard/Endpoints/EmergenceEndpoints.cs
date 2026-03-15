using AniRuntime.Emergence;
using AniRuntime.Emergence.Models;
using Microsoft.Extensions.Options;

namespace AniRuntime.Dashboard.Endpoints;

public static class EmergenceEndpoints
{
    public static RouteGroupBuilder MapEmergenceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/emergence").WithTags("Emergence");

        group.MapGet("/enabled", (IOptions<EmergenceOptions> opts) =>
        {
            return Results.Ok(new { enabled = opts.Value.Enabled });
        });

        group.MapGet("/stats", async (IServiceProvider sp, CancellationToken ct) =>
        {
            var store = sp.GetService<EmergenceStore>();
            if (store is null)
                return Results.Ok(new EmergenceStats());

            var stats = await store.GetStatsAsync(ct);
            return Results.Ok(stats);
        });

        group.MapGet("/resonance", async (
            IServiceProvider sp,
            int limit = 50,
            CancellationToken ct = default) =>
        {
            var store = sp.GetService<EmergenceStore>();
            if (store is null)
                return Results.Ok(Array.Empty<ResonanceRecord>());

            var records = await store.GetResonanceRecordsAsync(limit, ct);
            return Results.Ok(records);
        });

        group.MapGet("/log", async (
            IServiceProvider sp,
            int limit = 50,
            CancellationToken ct = default) =>
        {
            var store = sp.GetService<EmergenceStore>();
            if (store is null)
                return Results.Ok(Array.Empty<EmergenceLogEntry>());

            var entries = await store.GetRecentLogEntriesAsync(limit, ct);
            return Results.Ok(entries);
        });

        return group;
    }
}
