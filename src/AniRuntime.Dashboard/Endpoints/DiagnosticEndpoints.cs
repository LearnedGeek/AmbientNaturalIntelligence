using AniRuntime.Core.Interfaces;

namespace AniRuntime.Dashboard.Endpoints;

public static class DiagnosticEndpoints
{
    public static RouteGroupBuilder MapDiagnosticEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/diagnostic").WithTags("Diagnostic");

        group.MapGet("/", async (IDiagnosticService diagnostic, CancellationToken ct) =>
        {
            var report = await diagnostic.RunDiagnosticAsync(ct);
            return Results.Ok(report);
        });

        return group;
    }
}
