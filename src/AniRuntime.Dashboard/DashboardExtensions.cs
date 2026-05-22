using AniRuntime.Dashboard.Endpoints;

namespace AniRuntime.Dashboard;

public static class DashboardExtensions
{
    /// <summary>
    /// Maps all dashboard API endpoints and configures Blazor Server middleware.
    /// Call from Program.cs after app.Build().
    /// </summary>
    public static WebApplication MapDashboard(this WebApplication app)
    {
        // REST API endpoints
        app.MapAniStateEndpoints();
        app.MapMemoryEndpoints();
        app.MapConversationEndpoints();
        app.MapJournalEndpoints();
        app.MapContradictionEndpoints();
        app.MapEmergenceEndpoints();
        app.MapDiagnosticEndpoints();
        // Issue #41 Path B (2026-05-21) — per-turn expression classifications
        // surfaced via REST. Replaces the JSON file-system path in Research.razor.
        app.MapExpressionClassificationEndpoints();

        // Blazor Server (when components are added)
        app.MapBlazorHub();
        app.MapFallbackToPage("/_Host");

        return app;
    }

    /// <summary>
    /// Registers dashboard services (Blazor Server, etc.) into the DI container.
    /// Call from Program.cs during builder.Services configuration.
    /// </summary>
    public static IServiceCollection AddDashboard(this IServiceCollection services)
    {
        services.AddRazorPages();
        services.AddServerSideBlazor();
        return services;
    }
}
