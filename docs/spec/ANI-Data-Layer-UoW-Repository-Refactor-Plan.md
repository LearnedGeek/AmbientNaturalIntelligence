# ANI — Data Layer Refactor: EF Core + Unit of Work + Repository Pattern

**Status:** Draft. Awaiting Mark's review before Phase 1 execution.
**Date:** 2026-05-17 evening
**Author:** Claude (with Mark as architect-of-record)
**Why this plan exists:** Today's Phase 6 v1.2 work surfaced a structural bug — `ReflectionPhase` calls `SaveAsync` and `MarkCompressedAsync` as two separate operations, each with its own transaction. When `SaveAsync`'s Feature 30 dedup merges the new gist into an existing record, the pre-generated ID becomes orphan, `MarkCompressedAsync` fails on FK constraint, and the source records silently don't get marked `Compressed`. The deeper issue: **`SqliteMemoryService` is a ~2,000-line monolith using raw `Microsoft.Data.Sqlite` with no Unit of Work, no entity tracking, no atomic composition across method boundaries.** Every cross-cutting flow that composes two interface calls has the same fragility. This plan replaces the data layer with a proper EF Core + UoW + Repository implementation.

---

## §1 The Architectural Diagnosis

**Current state (as of 2026-05-17):**

- `src/AniRuntime.Memory/SqliteMemoryService.cs` — one class, ~2,000 lines, ~47 public methods, direct `SqliteConnection`/`SqliteCommand`/`SqliteTransaction` usage throughout.
- Interfaces `IMemoryPersistence` and `IMemorySearch` exist (March 19 SOLID refactor) but front the same monolithic implementation.
- No EF Core. No DbContext. No entity classes (we use POCO `MemoryRecord` mapped manually inside `ReadRecordsAsync`).
- No Unit of Work. Every method opens its own connection and transaction. Multi-operation flows (reflection's save-then-compress, conversation's reply-then-link, etc.) cannot be atomic across method boundaries.
- Schema is initialized via raw SQL `CREATE TABLE IF NOT EXISTS` in `InitialiseSchema`. No migrations infrastructure. Schema evolution has been hand-written ALTER statements (some inline, some via PowerShell scripts).
- Tests use shared-cache in-memory SQLite. Cross-test interference visible as flaky failures (the `MarkCompressedAsync_SetsTierAndCreatesProvenanceLinks` test that's been failing then passing on retry).

**Likely failure shape this has been masking:**

- Save-side dedup races
- Inconsistent state after partial failures (UPDATE succeeds, subsequent INSERT fails silently)
- The "record was there a second ago" bugs
- Test flake from shared in-memory cache
- The Phase 6 v1.2 gist-Compressed marking bug found today

**Same shape as Posture S (May 16) and Phase 6 v1.2 (May 17):** patched-around symptom for months because the underlying architectural seam wasn't right. This refactor closes the loop on the data-access layer.

---

## §2 Target Architecture

**Stack:** EF Core 8 with the SQLite provider. Code-first entity definitions with migrations.

**Layers:**

1. **Entity layer** (`AniRuntime.Memory.Entities/`):
   - POCO entity classes mirroring current schema (MemoryRecord, MemoryLink, CharacterState, DesireState, EmotionalState, ConfabulationFlag, ConversationMessage, ClosedConversationRecord, etc.)
   - Data annotations or Fluent API config for keys, indices, FK constraints.
   - Same SQLite schema underneath — we are NOT changing the DB shape, only the access pattern.

2. **DbContext** (`AniRuntime.Memory.AniDbContext`):
   - `DbSet<MemoryRecord>`, `DbSet<MemoryLink>`, etc.
   - `OnModelCreating` configures schema (matches current `InitialiseSchema` output exactly).
   - Connection string and options injected via DI.
   - **EF's `SaveChangesAsync` is the natural Unit of Work.** Composite operations either succeed atomically or roll back.

3. **Repository layer** (`AniRuntime.Memory.Repositories/`):
   - `IMemoryRepository`, `IMemoryLinkRepository`, `ICharacterStateRepository`, `IEmotionalStateRepository`, etc.
   - One repo per aggregate. Each repo holds a `AniDbContext` reference.
   - Repos expose intent-oriented methods (`AddRecord`, `MarkCompressed`, `LinkMemories`) — they enqueue changes on the DbContext but DO NOT call SaveChangesAsync themselves.
   - The Unit of Work decides when to commit.

4. **Unit of Work** (`AniRuntime.Memory.IUnitOfWork`):
   - Wraps DbContext lifetime.
   - Exposes repository properties.
   - `CommitAsync()` calls `DbContext.SaveChangesAsync()`.
   - `Dispose` rolls back if not committed.
   - Multi-operation flows acquire one UoW, work through repositories, commit once.

5. **Service layer** (existing `IMemoryPersistence` and `IMemorySearch` interfaces preserved):
   - Implementations now use UoW + Repositories internally.
   - Existing callers (CognitiveCycleProcessor, ReflectionPhase, OutreachPhase, etc.) are unchanged.
   - For composite flows that need atomicity (gist+compress, save+link, etc.) we add new methods that open one UoW and commit at the end.

**Raw SQL escape hatch:**

EF Core's LINQ doesn't model vector cosine similarity cleanly. The existing `SearchAsync`/`SearchWithScoresAsync` brute-force the cosine in C# after loading candidates. That stays — EF can still SELECT records via `FromSqlRaw` or by loading them via a query and computing cosine post-load (same as today). Raw SQL is fine *within* the repository layer; what matters is that composite operations are wrapped in a UoW.

---

## §3 Phasing Strategy

Six phases. Each phase produces a working, deployable build. No phase leaves the system half-migrated.

### Phase 1 — Discovery + entity scaffold (~1 session)

**Deliverables:**
- Full inventory of `SqliteMemoryService` methods grouped by aggregate (memory, links, character_state, etc.)
- Inventory of all SQL the service issues (SELECT/INSERT/UPDATE/DELETE patterns)
- Inventory of all multi-operation flows (where atomic composition is needed)
- EF Core packages added to projects: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`, etc.
- POCO entity classes drafted (mirror existing schema exactly)
- `AniDbContext` skeleton with DbSets and OnModelCreating
- Initial EF migration that matches current production DB schema (so production DB is "EF-aware" after first run)
- Schema diff verified — EF's generated schema matches what's currently in production with no drift

**Acceptance:** EF Core can open the production DB, query existing records via `dbContext.MemoryRecords.FirstOrDefault()`, and confirm content matches what raw SQL returns.

**No production change yet.**

### Phase 2 — Repository implementations behind existing interfaces (~1 session)

**Deliverables:**
- One repository class per aggregate (MemoryRepository, MemoryLinkRepository, CharacterStateRepository, ...)
- Each repo implements intent-oriented methods using DbContext operations
- `IUnitOfWork` interface + `UnitOfWork` implementation
- Existing `IMemoryPersistence` / `IMemorySearch` interfaces remain unchanged
- NEW alternative implementation: `EfMemoryService` — same surface as `SqliteMemoryService` but composes repositories + UoW under the hood
- DI configured so `EfMemoryService` is OPT-IN behind a flag (`AniOptions.UseEfDataLayer` default false). Old `SqliteMemoryService` remains the default.

**Acceptance:** Full test suite passes with `UseEfDataLayer=true`. All existing callers work unchanged because the interface surface is preserved.

**No production deploy yet** — flag stays false in production until Phase 4 cutover.

### Phase 3 — Atomic composite operations (~1 session)

**Deliverables:**
- New methods on `IMemoryPersistence` for atomic flows (or one method on `EfMemoryService`):
  - `SaveReflectionGistAndCompressAsync(gistContent, sourceIds)` — one UoW, decides insert-vs-merge, soft-deletes sources, creates links, commits
  - Other multi-operation flows identified in Phase 1
- `ReflectionPhase` updated to call the atomic method instead of composing `SaveAsync` + `MarkCompressedAsync`
- Tests for atomic behavior: partial failure rolls back, success commits all changes

**Acceptance:** The Phase 6 v1.2 gist-Compressed bug found today is empirically fixed. Tests verify that when dedup merge happens, the source records still get marked Compressed correctly.

### Phase 4 — Production cutover (~1 session)

**Deliverables:**
- Flip `AniOptions.UseEfDataLayer` to true in production config
- Deploy, monitor for regressions
- Backup taken; rollback plan documented (flip flag back, redeploy old bits)
- Validate via existing health endpoint and dashboard

**Acceptance:** Production runs cleanly on EF data layer for 24 hours. No new error log spikes. Reflection cycle fires and correctly marks sources Compressed (the bug is empirically fixed in production).

### Phase 5 — Remove old SqliteMemoryService (~1 session, smaller)

**Deliverables:**
- Delete `SqliteMemoryService.cs` (or move to archive folder for historical reference)
- Remove the flag — EF data layer is the only path
- DI registration simplified
- All tests updated to use the EF implementation directly

**Acceptance:** Build clean. Test suite passes. `SqliteMemoryService` no longer exists.

### Phase 6 — Migrations infrastructure formalization (~1 session, smaller)

**Deliverables:**
- All hand-written schema changes converted to EF migrations
- Migration history table in DB
- Documentation on how to add new migrations going forward (`dotnet ef migrations add ...`)
- The legacy `InitialiseSchema` raw-SQL path removed

**Acceptance:** A new developer (or future Claude) can add a new column by editing the entity class and running `dotnet ef migrations add` — no hand-written SQL.

---

## §4 Risks and Mitigations

| Risk | Mitigation |
|---|---|
| EF generates a different schema than current DB | Phase 1 acceptance includes schema diff verification. If EF generates something different, we adjust entity Fluent API config until they match exactly. |
| SQLite-specific behavior (WAL mode, BUSY, lock retry) | EF's SQLite provider supports these. We carry over current connection-string settings (`Cache=Shared`, `Foreign Keys=True`). |
| In-memory test isolation | EF Core's in-memory provider has different semantics than SQLite in-memory. Tests stay on SQLite in-memory but the shared-cache flake should improve (each test instance gets its own DbContext, lifecycle is explicit). |
| Vector embedding column (BLOB) | EF Core handles `byte[]` natively; we convert `float[]` ↔ `byte[]` in entity property accessors. Same as current code does. |
| Cosine similarity not LINQ-expressible | Repositories use `FromSqlRaw` or load candidates and compute cosine in C# (same as today). Atomicity is at the *transaction* level, not the query level. |
| Performance regression | Phase 2 acceptance includes a performance benchmark. EF Core has measurable overhead vs raw ADO.NET; if it's >2x for our workload we revisit. (Our workload is low-frequency — cognitive cycles every ~5-30 min — so EF overhead is unlikely to matter.) |
| Mid-flight production failure during cutover | Phase 4 keeps the flag-gated rollback. If we see issues post-deploy, flip the flag, redeploy, return to known-good state. |
| Existing 10K+ production records | No data migration needed — schema is unchanged. EF will read existing records as-is. |
| Multi-month behavior drift between old and new implementation | Phase 2 acceptance requires test-suite parity. Phase 4 cutover has a 24-hour observation window. |

---

## §5 What's NOT in Scope

- **No DB schema change.** Production tables stay as they are.
- **No data migration.** Existing records keep their content, IDs, tiers, links.
- **No new gates or behaviors.** This is a pure refactor of the data-access layer.
- **No interface change to `IMemoryPersistence` or `IMemorySearch`** in the early phases. New atomic methods get ADDED in Phase 3; existing methods stay.
- **No changes to callers** in early phases. They continue to use `IMemoryPersistence` and `IMemorySearch` unchanged.
- **Vector similarity computation** stays in C# brute-force (no migration to sqlite-vec extension or pgvector — that's a separate workstream).
- **No removal of the rumination guard, Feature 30 dedup, or other invariants** — those move into the repository/atomic methods unchanged. The behavior is preserved; only the composition mechanism changes.

---

## §6 Phase 6 v1.2 Status During Refactor

The Phase 6 v1.2 work shipped today is partially functional:

- **R3.1 synthesis (Qwen-driven structured output):** WORKING. New reflection records are register-tagged short summaries (verified via probe + sample inspection).
- **R1 decay-threshold trigger:** WORKING. Reflection fires on eligible records.
- **R2 soft-delete for non-reflection sources:** WORKING. ~1,490 records correctly marked Compressed.
- **R2 soft-delete for reflection sources:** BROKEN due to the bug this refactor addresses.
- **Old-reflection migration:** PARTIAL. Predicate selects them correctly; soft-delete doesn't propagate due to the bug.

After Phase 3 (atomic composite operation), R2 soft-delete works for reflection sources too. Then we re-run the migration scan and old-shape reflections get properly compressed.

**Pause point:** the system is in a working-but-degraded state. New gists are produced correctly with v1.2 shape; sources get marked Compressed when synthesis doesn't trigger Feature 30 dedup. The bug only manifests when dedup fires, which happens more often as v1.2 gist count grows. **Not actively damaging substrate** — just not making the progress we wanted on the migration.

Layers 2-5 (Vibe Loop V1.5b, Posture-S+1, confab-gate rehab, KPI) are paused pending refactor completion.

---

## §7 Estimated Total Effort

- Phase 1: ~3-4 hours
- Phase 2: ~4-5 hours
- Phase 3: ~2-3 hours
- Phase 4: ~1-2 hours (mostly observation)
- Phase 5: ~1 hour
- Phase 6: ~2-3 hours

**Total: ~13-18 hours of focused refactor work spread across 3-5 sessions.**

---

## §8 Decision Pending

Mark's review of this plan + go-ahead on Phase 1 execution. No code change starts until plan is approved.

If you want to adjust the phasing (e.g., combine phases, defer Phase 6, etc.) say so. The phases are designed to each be a coherent stopping point — we can pause between any two phases without leaving the system in a broken state.
