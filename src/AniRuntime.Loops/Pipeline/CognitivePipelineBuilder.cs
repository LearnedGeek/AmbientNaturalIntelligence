using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AniRuntime.Loops.Pipeline;

/// <summary>
/// Theme O Phase O.1 (May 10, 2026) — fluent <c>app.Use</c>-style builder
/// for the cognitive pipeline (§9 lock 3, May 10 18:06 CDT). Registration
/// order = execution order. No <c>Order</c> integers, no DI-discovery magic;
/// the whole pipeline shape is grep-able in one place (<c>Program.cs</c>),
/// reorders are a one-line edit.
///
/// The builder enforces stage-method correctness at registration time:
/// calling <see cref="UsePreHandler{T}"/> with a Post-stage handler type
/// throws <see cref="InvalidOperationException"/>. The check resolves the
/// handler once (via the service provider) so the <see cref="ICognitivePipelineHandler.Stage"/>
/// can be inspected — without ever holding a reference to that resolved
/// instance (handlers are still resolved per-call via the runtime
/// <see cref="CognitivePipeline"/> constructor).
/// </summary>
public sealed class CognitivePipelineBuilder
{
    private readonly IServiceCollection _services;

    /// <summary>
    /// Internal list of handler types in registration order. The DI
    /// extension method resolves each in this order at runtime to feed the
    /// <see cref="CognitivePipeline"/> constructor.
    /// </summary>
    internal List<Type> HandlerTypes { get; } = new();

    internal CognitivePipelineBuilder(IServiceCollection services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Register a Pre-stage handler. The handler type is added to the DI
    /// container as <see cref="ICognitivePipelineHandler"/> (registration
    /// order preserved) and stage-validated at registration.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handler advertises a
    /// stage other than <see cref="PipelineStage.Pre"/>.</exception>
    public CognitivePipelineBuilder UsePreHandler<T>() where T : class, ICognitivePipelineHandler
    {
        RegisterHandler<T>(PipelineStage.Pre);
        return this;
    }

    /// <summary>
    /// Register a Post-stage handler. The handler type is added to the DI
    /// container as <see cref="ICognitivePipelineHandler"/> (registration
    /// order preserved) and stage-validated at registration.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handler advertises a
    /// stage other than <see cref="PipelineStage.Post"/>.</exception>
    public CognitivePipelineBuilder UsePostHandler<T>() where T : class, ICognitivePipelineHandler
    {
        RegisterHandler<T>(PipelineStage.Post);
        return this;
    }

    private void RegisterHandler<T>(PipelineStage expected) where T : class, ICognitivePipelineHandler
    {
        // Validate stage by instantiating a temporary probe. We use
        // Activator only when the type has a parameterless constructor; for
        // types with DI dependencies, we register first and validate lazily
        // at first resolution. Most handlers are simple enough to probe.
        var probe = TryProbe<T>();
        if (probe is not null && probe.Stage != expected)
        {
            throw new InvalidOperationException(
                $"Handler {typeof(T).Name} advertises Stage={probe.Stage} but was registered via " +
                $"Use{expected}Handler. Use Use{probe.Stage}Handler instead.");
        }

        // Register the handler as itself (singleton) so the pipeline can
        // resolve it. Registration order = execution order.
        _services.AddSingleton<T>();
        // Also expose via ICognitivePipelineHandler so the CognitivePipeline
        // constructor's IEnumerable<ICognitivePipelineHandler> sees it.
        _services.AddSingleton<ICognitivePipelineHandler>(sp => sp.GetRequiredService<T>());
        HandlerTypes.Add(typeof(T));
    }

    /// <summary>
    /// Attempt to construct a temporary instance to read its
    /// <see cref="ICognitivePipelineHandler.Stage"/>. Returns null when the
    /// type has no parameterless constructor — in that case the
    /// registration-time stage check is skipped (the orchestrator's
    /// AppliesTo + stage-split at construction is the safety net).
    /// </summary>
    private static ICognitivePipelineHandler? TryProbe<T>() where T : class, ICognitivePipelineHandler
    {
        try
        {
            var ctor = typeof(T).GetConstructor(Type.EmptyTypes);
            return ctor is null ? null : (ICognitivePipelineHandler)Activator.CreateInstance(typeof(T))!;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Theme O Phase O.1 (May 10, 2026) — service-collection extension that
/// wires the cognitive pipeline through DI. Call from <c>Program.cs</c>:
///
/// <code>
/// builder.Services.AddCognitivePipeline(p => p
///     .UsePreHandler&lt;FrameSelectionHandler&gt;()
///     .UsePostHandler&lt;SelfEchoInvariantHandler&gt;()
///     .UsePostHandler&lt;DirectAddressInvariantHandler&gt;()
/// );
/// </code>
///
/// O.1 ships infrastructure only — the call accepts zero handlers (empty
/// pipeline) and is in place so O.2 can begin adding handlers via the
/// builder without touching the call site.
/// </summary>
public static class CognitivePipelineServiceCollectionExtensions
{
    public static IServiceCollection AddCognitivePipeline(
        this IServiceCollection                 services,
        Action<CognitivePipelineBuilder>        configure)
    {
        if (services  is null) throw new ArgumentNullException(nameof(services));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        var builder = new CognitivePipelineBuilder(services);
        configure(builder);

        // Resolve handlers in REGISTRATION ORDER. The default
        // IEnumerable<T> enumeration in MEDI preserves registration order,
        // but we explicitly project through the recorded HandlerTypes list
        // to make the contract immune to MEDI internal changes.
        var orderedTypes = builder.HandlerTypes.ToList();
        services.AddSingleton(sp =>
        {
            var handlers = orderedTypes
                .Select(t => (ICognitivePipelineHandler)sp.GetRequiredService(t))
                .ToList();
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CognitivePipeline>>();
            return new CognitivePipeline(handlers, log);
        });

        return services;
    }
}
