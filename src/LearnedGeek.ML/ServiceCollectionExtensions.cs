using LearnedGeek.ML.Interfaces;
using LearnedGeek.ML.TagMapping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnedGeek.ML;

/// <summary>
/// DI registration for LearnedGeek.ML services.
/// Call AddLearnedGeekML() from any consuming project's service configuration.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register LearnedGeek.ML classification and tag mapping services.
    /// </summary>
    public static IServiceCollection AddLearnedGeekML(
        this IServiceCollection services,
        Action<MLOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);

        // Tag mapping: singleton — rules loaded once, reused across requests
        services.TryAddSingleton<ITagMappingService, TagMappingService>();

        // Classification: registered by the consumer when LM-Kit models are available.
        // If no implementation is registered, consumers should check for null/use feature flags.

        return services;
    }
}
