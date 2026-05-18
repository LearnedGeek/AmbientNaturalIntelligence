namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — focused contract for writing
/// memory-audit-log entries. Extracted from the legacy
/// <c>SqliteMemoryService.AuditAsync</c> private helper so consumers
/// (persistence, maintenance, future merge-policy) can write to the
/// audit log without coupling to the legacy implementation.
///
/// **Why this is its own service:** the audit log is the rollback safety
/// net for memory mutations (added April 5, 2026 after the auto-corrector
/// deleted 128 valid memories with no recovery). It has to be writable
/// from multiple call sites — create, update, delete, merge, restore —
/// and audit-write failures must never block the primary operation but
/// also must not be silent. Single-responsibility = "append to memory_audit
/// with snapshot context, log on failure, don't throw."
/// </summary>
public interface IMemoryAuditWriter
{
    /// <summary>
    /// Append an audit-log entry. Action values: "create" | "update" |
    /// "delete" | "merge". Source values: "cognitive-cycle" |
    /// "auto-corrector" | "merge" | "manual" | "import" | "restore".
    /// Before/after snapshots are nullable per the action shape — e.g.
    /// "delete" has no contentAfter; "create" has no contentBefore.
    /// </summary>
    /// <remarks>
    /// Implementation must NOT throw. Audit failure is logged and swallowed
    /// so the primary operation succeeds even if the audit log is
    /// unwritable (the rollback gap is logged for forensic recovery).
    /// </remarks>
    Task WriteAsync(
        Guid memoryId,
        string action,
        string source,
        string? contentBefore,
        string? contentAfter,
        int? typeBefore,
        int? typeAfter,
        float? importanceBefore,
        float? importanceAfter,
        CancellationToken ct = default);
}
