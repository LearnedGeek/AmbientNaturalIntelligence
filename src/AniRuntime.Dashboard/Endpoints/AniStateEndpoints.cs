using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Dashboard.Dtos;
using AniRuntime.Loops;

namespace AniRuntime.Dashboard.Endpoints;

public static class AniStateEndpoints
{
    public static RouteGroupBuilder MapAniStateEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/ani").WithTags("Ani State");

        group.MapGet("/status", async (
            IMemoryService memory,
            DesireEngine desire,
            CancellationToken ct) =>
        {
            var charState = await memory.GetCharacterStateAsync(ct);
            var emotional = await memory.GetEmotionalStateAsync(ct);
            var desireState = await desire.GetStateAsync(ct);
            var health = await memory.GetRelationshipHealthAsync(ct);

            return Results.Ok(new AniStatusDto
            {
                Name = charState.Name,
                PrimaryContact = charState.PrimaryContactName,
                PersonaVersion = charState.PersonaVersion,
                EmotionalState = new EmotionalStateDto
                {
                    Warmth = emotional.Warmth,
                    Energy = emotional.Energy,
                    Concern = emotional.Concern,
                    Playfulness = emotional.Playfulness,
                    Mood = emotional.Describe(),
                },
                DesireState = new DesireStateDto
                {
                    DesireToConnect = desireState.DesireToConnect,
                    CooldownActive = desireState.CooldownActive,
                    CooldownUntil = desireState.CooldownUntil,
                    CircadianModifier = desireState.CircadianModifier,
                    LastContactInbound = desireState.LastContactInbound,
                },
                RelationshipPhase = health?.Phase,
                ContactGapTension = emotional.ContactGapTension,
            });
        });

        group.MapGet("/emotional-state", async (IMemoryService memory, CancellationToken ct) =>
        {
            var emotional = await memory.GetEmotionalStateAsync(ct);
            return Results.Ok(new EmotionalStateDto
            {
                Warmth = emotional.Warmth,
                Energy = emotional.Energy,
                Concern = emotional.Concern,
                Playfulness = emotional.Playfulness,
                Mood = emotional.Describe(),
            });
        });

        group.MapGet("/emotional-history", async (
            IMemoryService memory,
            int hours = 24,
            CancellationToken ct = default) =>
        {
            var history = await memory.GetEmotionalHistoryAsync(hours, ct);
            var dtos = history.Select(s => new EmotionalHistoryPointDto
            {
                Warmth = s.Warmth,
                Energy = s.Energy,
                Concern = s.Concern,
                Playfulness = s.Playfulness,
                Timestamp = s.RecordedAt,
            }).ToList();
            return Results.Ok(dtos);
        });

        group.MapGet("/character", async (IMemoryService memory, CancellationToken ct) =>
        {
            var charState = await memory.GetCharacterStateAsync(ct);
            return Results.Ok(charState);
        });

        group.MapGet("/desire", async (DesireEngine desire, CancellationToken ct) =>
        {
            var state = await desire.GetStateAsync(ct);
            return Results.Ok(new DesireStateDto
            {
                DesireToConnect = state.DesireToConnect,
                CooldownActive = state.CooldownActive,
                CooldownUntil = state.CooldownUntil,
                CircadianModifier = state.CircadianModifier,
                LastContactInbound = state.LastContactInbound,
            });
        });

        return group;
    }
}
