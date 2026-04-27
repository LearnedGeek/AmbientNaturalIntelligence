using AniRuntime.Core.Interfaces;
using AniRuntime.Emergence.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AniRuntime.Emergence;

public static class EmergenceExtensions
{
    /// <summary>
    /// Registers emergence layer services. When Emergence:Enabled is true,
    /// provides EmergenceObserver as IEmergenceObserver. Otherwise provides
    /// NullEmergenceObserver (zero cost).
    /// </summary>
    public static IServiceCollection AddEmergence(
        this IServiceCollection services,
        bool enabled)
    {
        if (enabled)
        {
            services.AddSingleton<EmergenceStore>();
            services.AddSingleton<IEmergenceObserver, EmergenceObserver>();
        }
        else
        {
            services.AddSingleton<IEmergenceObserver, NullEmergenceObserver>();
        }

        // EM9 longitudinal logger registers regardless of the main emergence
        // layer flag. It's a pure observer (logs only, never mutates) and the
        // research value depends on continuous accumulation. See backlog
        // item 15.15 + Active Work Plan item 4.
        services.AddSingleton<Em9Detector>();

        return services;
    }
}
