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

        // Classification: LM-Kit backed, lazy model loading on first use
        services.TryAddSingleton<ITextClassificationService, LMKitClassificationService>();

        // ML-powered voice tag enricher (bridges classification → v3 audio tags)
        services.TryAddSingleton<MLVoiceTagEnricher>();

        // Persona summary cache — ground truth for confabulation verification
        services.TryAddSingleton<PersonaSummaryCache>();

        // Comparison service for heuristic vs ML accuracy evaluation
        services.TryAddSingleton<ClassificationComparisonService>();

        return services;
    }
}
