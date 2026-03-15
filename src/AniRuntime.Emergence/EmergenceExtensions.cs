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

        return services;
    }
}
