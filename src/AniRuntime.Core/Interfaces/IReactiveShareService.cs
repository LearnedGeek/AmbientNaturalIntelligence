using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// RSS-driven reactive share flow — bypasses the desire engine when a
/// high-relevance perception arrives. Rate-limited via daily counter +
/// per-share cooldown. Returns <c>true</c> when a share was dispatched
/// (and the cycle should end), <c>false</c> otherwise.
///
/// <para>
/// Counter + last-day state live on the implementation (Singleton);
/// resets daily. Extracted from <c>OutreachPhase</c> in §5.3 SOLID
/// refactor so reactive share is independently testable and the
/// orchestrator doesn't hold its rate-limit fields.
/// </para>
/// </summary>
public interface IReactiveShareService
{
    Task<bool> TryShareAsync(
        List<PerceptionEvent> perceptions,
        CharacterStateDoc charState,
        CancellationToken ct);
}
