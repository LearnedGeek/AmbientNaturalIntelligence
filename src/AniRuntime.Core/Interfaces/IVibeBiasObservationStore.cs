using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Vibe Loop V1.5a (2026-05-21) — write/read surface for
/// <see cref="VibeBiasObservationRecord"/> instances. Narrow interface per
/// the ISP discipline; doesn't bloat <see cref="IClosedConversationStore"/>
/// (closed-record concern) with observation-pass concerns.
///
/// V1.5a ships <see cref="SaveAsync"/> + <see cref="GetRecentAsync"/> only.
/// Per-thread / per-callsite queries land on dedicated methods when the
/// dashboard / Gate 3 reviewer actually needs them, not pre-emptively.
/// See GitHub Issue #46.
/// </summary>
public interface IVibeBiasObservationStore
{
    /// <summary>
    /// Persist one observational pass. Idempotent on
    /// <see cref="VibeBiasObservationRecord.Id"/> — re-saving with the same
    /// id replaces the existing row (UPSERT semantics). Throws on SQLite
    /// failure; callers (the V1.5a observation helper) wrap this in a
    /// best-effort try/catch so persistence failure never propagates to
    /// dispatch.
    /// </summary>
    Task SaveAsync(VibeBiasObservationRecord record, CancellationToken ct = default);

    /// <summary>
    /// Most recent N observation records, ordered by
    /// <see cref="VibeBiasObservationRecord.ObservedAt"/> descending.
    /// Used by dashboard and Gate 3 review tooling to render the
    /// diversity histogram + recommended-vs-actual divergence triple.
    /// </summary>
    Task<IEnumerable<VibeBiasObservationRecord>> GetRecentAsync(int limit = 100, CancellationToken ct = default);
}
