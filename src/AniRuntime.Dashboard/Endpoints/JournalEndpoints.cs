using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Dashboard.Dtos;

namespace AniRuntime.Dashboard.Endpoints;

public static class JournalEndpoints
{
    public static RouteGroupBuilder MapJournalEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/journal").WithTags("Journal");

        group.MapGet("/", async (
            IMemoryService memory,
            int limit = 50,
            CancellationToken ct = default) =>
        {
            var thoughts = await memory.GetByTypeAsync(MemoryType.InnerThought, limit, ct);
            var dtos = thoughts.Select(r => new MemoryRecordDto
            {
                Id = r.Id,
                Type = r.Type.ToString(),
                Content = r.Content,
                Importance = r.Importance,
                RelationalValence = r.RelationalValence,
                SourceName = r.SourceName,
                CreatedAt = r.CreatedAt,
                OccurredAt = r.OccurredAt,
            }).ToList();
            return Results.Ok(dtos);
        });

        return group;
    }
}
