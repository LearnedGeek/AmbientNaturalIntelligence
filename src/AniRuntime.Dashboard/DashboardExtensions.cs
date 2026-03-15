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
