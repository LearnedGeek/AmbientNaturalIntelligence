using AniRuntime.Core.Interfaces;

namespace AniRuntime.Dashboard.Endpoints;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P8 (2026-08-23) — dashboard
/// endpoint that surfaces the substrate's attribution health. Backs the
/// dashboard tile that shows the (AttributedTo × AttributionTrust)
/// breakdown so a producer emit-site regression (attribution silently
/// dropping to Unknown/unverified) is visible without grepping journal
/// logs.
/// </summary>
public static class AttributionEndpoints
{
    public static RouteGroupBuilder MapAttributionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/attribution").WithTags("Attribution");

        group.MapGet("/distribution", async (
            IMemoryAnalytics analytics,
            CancellationToken ct) =>
        {
            var dist = await analytics.GetAttributionDistributionAsync(ct);

            // Project enum keys to strings for JSON friendliness. The
            // enum values are the actual property names (Ani/Mark/World/
            // Unknown) so consumers get a stable, greppable shape.
            var byAttributedTo = dist.ByAttributedTo
                .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);

            return Results.Ok(new
            {
                total          = dist.TotalRows,
                byAttributedTo,
                byTrust        = dist.ByTrust,
            });
        });

        return group;
    }
}
