using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Observes completed cognitive cycles for the emergence layer.
/// The implementation receives immutable snapshots — it cannot mutate runtime state.
/// Default is NullEmergenceObserver (no-op) when emergence is disabled.
/// </summary>
public interface IEmergenceObserver
{
    Task OnCycleCompleteAsync(CycleObservation observation, CancellationToken ct = default);
}

/// <summary>
/// No-op observer used when emergence is disabled. Zero runtime cost.
/// </summary>
public class NullEmergenceObserver : IEmergenceObserver
{
    public Task OnCycleCompleteAsync(CycleObservation observation, CancellationToken ct = default)
        => Task.CompletedTask;
}
