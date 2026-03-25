using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Dashboard.Dtos;

namespace AniRuntime.Dashboard.Endpoints;

public static class MemoryEndpoints
{
    public static RouteGroupBuilder MapMemoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/memories").WithTags("Memories");

        group.MapGet("/", async (
            IMemoryService memory,
            string? type = null,
            int limit = 50,
            CancellationToken ct = default) =>
        {
            IEnumerable<MemoryRecord> records;
            if (!string.IsNullOrEmpty(type) && Enum.TryParse<MemoryType>(type, true, out var memType))
                records = await memory.GetByTypeAsync(memType, limit, ct);
            else
                records = await memory.GetByTypeAsync(MemoryType.Semantic, limit, ct);

            var dtos = records.Select(MapToDto).ToList();
            return Results.Ok(dtos);
        });

        group.MapGet("/search", async (
            IMemoryService memory,
            string q,
            int limit = 10,
            CancellationToken ct = default) =>
        {
            var results = await memory.SearchAsync(q, limit, ct);
            var dtos = results.Select(MapToDto).ToList();
            return Results.Ok(dtos);
        });

        group.MapGet("/anchored", async (IMemoryService memory, CancellationToken ct) =>
        {
            var anchored = await memory.GetAnchoredMemoriesAsync(ct);
            var dtos = anchored.Select(MapToDto).ToList();
            return Results.Ok(dtos);
        });

        group.MapGet("/profile", async (IMemoryService memory, int limit = 100, CancellationToken ct = default) =>
        {
            var semantics = await memory.GetByTypeAsync(MemoryType.Semantic, limit, ct);
            var categorized = semantics.Select(r =>
            {
                var category = r.Content switch
                {
                    var c when c.StartsWith("About Mark:") => "About Mark",
                    var c when c.StartsWith("About Ani:") => "About Ani",
                    var c when c.StartsWith("Interest:") => "Interests",
                    var c when c.StartsWith("Shared experience:") => "Shared Experiences",
                    _ => "Other"
                };
                return new
                {
                    Id = r.Id,
                    Category = category,
                    Content = r.Content,
                    Importance = r.Importance,
                    IsAnchored = r.AnchoredAt.HasValue,
                    CreatedAt = r.CreatedAt,
                };
            })
            .Where(r => r.Category != "Other")
            .OrderBy(r => r.Category)
            .ThenByDescending(r => r.Importance)
            .ToList();

            return Results.Ok(categorized);
        });

        group.MapGet("/graph", async (
            IMemoryService memory,
            IMemoryMaintenance maintenance,
            CancellationToken ct) =>
        {
            var allMemories = new List<MemoryRecord>();
            foreach (var type in Enum.GetValues<MemoryType>())
            {
                var records = await memory.GetByTypeAsync(type, 500, ct);
                allMemories.AddRange(records);
            }

            var links = await maintenance.GetAllLinksAsync(ct);

            var nodes = allMemories.Select(r => new
            {
                id = r.Id.ToString(),
                type = r.Type.ToString(),
                content = r.Content.Length > 150 ? r.Content[..150] + "..." : r.Content,
                importance = r.Importance,
                isAnchored = r.AnchoredAt.HasValue,
                createdAt = r.CreatedAt,
            }).ToList();

            var edges = links.Select(l => new
            {
                source = l.SourceId.ToString(),
                target = l.TargetId.ToString(),
                relationship = l.Relationship,
            }).ToList();

            return Results.Ok(new { nodes, edges });
        });

        return group;
    }

    private static MemoryRecordDto MapToDto(MemoryRecord r) => new()
    {
        Id = r.Id,
        Type = r.Type.ToString(),
        Content = r.Content,
        Importance = r.Importance,
        RelationalValence = r.RelationalValence,
        SourceName = r.SourceName,
        IsAnchored = r.AnchoredAt.HasValue,
        CreatedAt = r.CreatedAt,
        OccurredAt = r.OccurredAt,
    };
}
