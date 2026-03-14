using AniRuntime.Core.Interfaces;
using AniRuntime.Dashboard.Dtos;

namespace AniRuntime.Dashboard.Endpoints;

public static class ConversationEndpoints
{
    public static RouteGroupBuilder MapConversationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/conversations").WithTags("Conversations");

        group.MapGet("/active", async (IConversationService conversations, CancellationToken ct) =>
        {
            var thread = await conversations.GetActiveThreadAsync(ct);
            if (thread is null)
                return Results.NotFound();
            return Results.Ok(MapToDto(thread));
        });

        group.MapGet("/recent", async (
            IConversationService conversations,
            int limit = 10,
            CancellationToken ct = default) =>
        {
            var threads = await conversations.GetRecentThreadsAsync(limit, ct);
            var dtos = threads.Select(MapToDto).ToList();
            return Results.Ok(dtos);
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            IConversationService conversations,
            CancellationToken ct) =>
        {
            var thread = await conversations.GetThreadAsync(id, ct);
            if (thread is null)
                return Results.NotFound();
            return Results.Ok(MapToDto(thread));
        });

        return group;
    }

    private static ConversationThreadDto MapToDto(AniRuntime.Core.Models.ConversationThread t) => new()
    {
        Id = t.Id,
        StartedAt = t.StartedAt,
        EndedAt = t.IsActive ? null : t.LastMessageAt,
        MessageCount = t.Messages.Count,
        Messages = t.Messages.Select(m => new ConversationMessageDto
        {
            Role = m.Role,
            Content = m.Content,
            SentAt = m.SentAt,
        }).ToList(),
    };
}
