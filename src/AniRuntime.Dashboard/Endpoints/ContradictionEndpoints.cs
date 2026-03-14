using AniRuntime.Core.Interfaces;

namespace AniRuntime.Dashboard.Endpoints;

public static class ContradictionEndpoints
{
    public static RouteGroupBuilder MapContradictionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/contradictions").WithTags("Contradictions");

        group.MapGet("/", async (
            IMemoryService memory,
            bool includeResolved = false,
            CancellationToken ct = default) =>
        {
            var contradictions = await memory.GetFlaggedContradictionsAsync(includeResolved, ct);
            return Results.Ok(contradictions);
        });

        group.MapPost("/{newId:guid}/{existingId:guid}/resolve", async (
            Guid newId,
            Guid existingId,
            IMemoryService memory,
            CancellationToken ct) =>
        {
            await memory.ResolveContradictionAsync(newId, existingId, ct);
            return Results.Ok();
        });

        return group;
    }
}
