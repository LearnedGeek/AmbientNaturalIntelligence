# UltraReview — `src/AniRuntime.Memory` + `IMemory*` interfaces

**Date:** 2026-04-18
**Reviewer:** OC (Claude Opus 4.7)
**Scope:** `SqliteMemoryService.cs` (2417 L), `SqliteConversationService.cs` (354 L), `ProvenanceBackfill.cs`, the five `IMemory*` interfaces.
**Rubric:** `~/.claude/CLAUDE.md` (concurrency, SQL correctness, lifecycle, merge, cache coherence).
**Changes made:** none — review-only.

> Context note: `SqliteMemoryService` is registered **singleton** and shared across cognitive cycle, Twilio inbound, voice, and dashboard (`src/AniRuntime.Service/Program.cs:70-76`). All findings should be read against that concurrency surface.

---

## CRITICAL

### C1. Foreign-key constraints are declared but never enforced
**Where:** `SqliteMemoryService.cs:1940-1948` (FK decl), `1833-1838` (`OpenAsync`), `1840-1850` (`InitialiseSchema`).
**What:** `memory_links` declares `FOREIGN KEY (source_id/target_id) REFERENCES memories(id)`. `Microsoft.Data.Sqlite` does **not** enable FK enforcement by default — it must be set per connection (`Foreign Keys=True` in the connection string, or `PRAGMA foreign_keys=ON` immediately after `Open()`). Neither is done. The comment at `SqliteMemoryService.cs:140-146` explicitly invokes "FOREIGN KEY constraint failures" as justification for a bug fix — that defense is not active in production.
**Impact:** Orphaned links in `memory_links` after any delete/merge that bypasses `ReassignMemoryLinksAsync`. Silent data drift. The merge path does not bypass — it mutates in place — but the `DeleteAsync` path (line 1169) deletes links first, which is defensive. The hazard is any future code path (admin/import/restore) that deletes a memory without deleting its links.
**Fix:** append `;Foreign Keys=True` to `_connectionString`, or run `PRAGMA foreign_keys=ON` inside `OpenAsync`. Do this once; verify existing orphans with a one-time integrity sweep.

### C2. `ReassignMemoryLinksAsync` is dead code — merges silently stop updating links
**Where:** definition `SqliteMemoryService.cs:842-908`. Grep for the name across the entire repo: only the definition matches.
**What:** When `MergeMemoriesAsync` updates the surviving record in place (`UPDATE memories SET content=…`, line 637-647), the *incoming* `record.Id` is **never** used to reassign links — because the incoming record was never inserted. `SaveAsync` line 144 temporarily swaps `record.Id` to the survivor id only to feed `CreateLinksAsync`. That's fine for forward-link creation, but any pre-existing links whose source or target equalled the **incoming** id would be orphans — except the incoming id never existed in the DB, so there are none. So current code is accidentally correct for the same-type merge path.
**But:** the cross-type correction path (line 708-774) calls `MergeMemoriesAsync` with the new record's content merged into an *existing* Semantic profile record. If the incoming record was already inserted somewhere else in the system (e.g., an Episodic save earlier in the cycle), its links will not be reassigned. More importantly, `ReassignMemoryLinksAsync` being dead means you have no tested path for the future when duplicate detection in `RebuildMemoryLinksAsync` is upgraded from "log" to "merge."
**Fix:** either delete the dead helper with a comment explaining why merge-in-place doesn't need it, or wire it in so `RebuildMemoryLinksAsync` can actually perform the merges it currently only logs (line 1504-1512 counts "duplicates" but takes no action).

### C3. `SaveAsync` is not transactional across its 3 subqueries
**Where:** `SqliteMemoryService.cs:100-180`.
**What:** `FindMergeCandidateAsync` (own connection), `MergeMemoriesAsync` (own connection), `InsertMemoryAsync` (own connection, does SELECT-then-INSERT OR REPLACE), `CreateLinksAsync` (own connection). Four separate connections, no transaction. Under the singleton + multi-entry-point reality:
- Two concurrent `SaveAsync` calls with near-duplicate content can both pass `FindMergeCandidate` with no hit (they see each other as not-yet-written), then both insert. Dedup is racy.
- The SELECT-before-INSERT in `InsertMemoryAsync` (line 219-234) used only for audit classification (`create` vs `update`) is vulnerable to the same race. Impact is audit-log fidelity, not data, but you rely on that log for rollback (line 1960 comment).
- `MergeMemoriesAsync` does UPDATE with no WHERE-clause guard on `content` or `updated_at` — a concurrent merge can clobber a concurrent merge.

**Fix:** wrap the `FindMergeCandidate → Merge | Insert → CreateLinks` sequence in `BEGIN IMMEDIATE`/commit on a single connection, OR gate `SaveAsync` with a `SemaphoreSlim(1,1)` at the service level. Given save cadence (cognitive cycles + inbound), a single semaphore is cheap and matches the singleton shape.

---

## HIGH

### H1. `GetLinkedMemoryIdsAsync` builds SQL via string concatenation
**Where:** `SqliteMemoryService.cs:957-963`.
```csharp
var idList = string.Join(",", sourceIds.Select(id => $"'{id}'"));
cmd.CommandText = $"""… WHERE source_id IN ({idList}) …""";
```
**What:** All current callers pass GUID strings from our own DB, so injection is theoretical today — but this violates the CLAUDE.md rule (OWASP SQL injection) and is landmine-prone: a future caller passing a raw user string would be exploitable. There is also no parameter cap, so a pathological caller blows the compiled-statement size.
**Fix:** parameterize with `$id0, $id1, …` or insert into a temp table and JOIN.

### H2. Merge preserves `provenance` of survivor but lets Episodic content flow into Facts
**Where:** `TryCrossTypeProfileCorrectionAsync` (line 708-774) + `MergeMemoriesAsync` (line 604-666).
**What:** The guard at line 715-718 blocks records whose content starts with "I said to"/"I reached out to" (Ani speaker). Good. But:
- The filter is **content-prefix based**, not provenance-based. Records whose content doesn't start with those literals but represent Ani's voice (any future speaker template change) would bypass.
- `MergeMemoriesAsync` UPDATE at line 637-641 updates `content, embedding, occurred_at` only. `provenance` stays whatever the survivor had, but the new **semantic content** is now partly Episodic conversation text stored in a Facts-tier row. You have cleanly separated tiers at retrieval (`SearchByTierAsync` line 1093 filters by provenance) yet the merge path quietly contaminates Facts-tier content with Episodic material.
- Since the cross-type correction uses the LLM-rewritten output, that output may legitimately be fact-shaped — but `ContainsNovelSpecifics` (line 676-700) only gates against *added* specifics, not against *speaker register* drift.

**Fix:** Either (a) require `record.Provenance == targetProvenance` in the cross-type path (rejecting Episodic-into-Facts), or (b) re-classify the merged content against `ProvenanceBackfill.ClassifyProvenance` post-merge and reject if tier changed. Add a test: Episodic record with profile-shaped content must not mutate Facts memory.

### H3. `MergeMemoriesAsync` bumps `occurred_at` to `UtcNow` — merges never age out
**Where:** `SqliteMemoryService.cs:645`.
**What:** `occurred_at` drives the recency-decay term (`ComputeRetrievalScore` line 513-515) and the `FindMergeCandidateAsync` ORDER BY. Every merge moves the record to "just happened," making it the perennial top candidate for the next near-duplicate. Result: hot memories accrete merges indefinitely and effectively never decay — directly contradicting the Park et al. recency model Feature 20 implements.
**Fix:** preserve original `occurred_at` (or use MIN(existing, incoming) when both are meaningful). If you want a "last touched" signal, add a separate `merged_at` column and use it only for audit, not retrieval.

### H4. `InsertMemoryAsync` uses `INSERT OR REPLACE` — bug-factory for upsert
**Where:** `SqliteMemoryService.cs:190-199`.
**What:** `INSERT OR REPLACE` on a row with FKs *deletes* the existing row, which cascade-deletes any FK references. If FK enforcement is ever turned on (see C1), `memory_links` for that id would be deleted on every save (every save is effectively an upsert via REPLACE). If FK is off, `raw_json`, `created_at`, `importance` etc. are silently reset to whatever the incoming record carries — including possibly clobbering a merged record because `SaveAsync` does not actually detect "I just merged into the survivor and now I'm going to re-INSERT with the original new-record values."

Actually re-reading: after a successful merge, `SaveAsync` returns at line 147 before reaching `InsertMemoryAsync`. So this specific clobber doesn't happen. Still: every save that gets a cache miss in `FindMergeCandidate` and happens to collide on an existing PK (admin backfill, restore, re-save) silently replaces the row. Callers that "just update importance" should use the `AdjustImportanceAsync` path; the concern is the general pattern invites drift.
**Fix:** switch to `INSERT INTO ... ON CONFLICT(id) DO UPDATE SET ... RETURNING …`, and only update the fields the write is intended to change. `SqliteConversationService.cs:137-140` already uses this pattern — mirror it.

### H5. Audit log: silent-swallow pattern
**Where:** `AuditAsync` `SqliteMemoryService.cs:2275-2278`.
```csharp
catch { /* Audit failure must never block the primary operation */ }
```
**What:** Violates CLAUDE.md "don't swallow exceptions with empty catch blocks." The audit log exists because you lost 128 memories before (per the table comment). Silently dropping audit rows means you'd lose a future 128-memory incident *and* not know the log was broken.
**Fix:** at minimum `catch (Exception ex) { _log.LogWarning(ex, "Audit write failed for memory {Id} action {Action}", memoryId, action); }`. Pass `ILogger` into the helper, or make it an instance method.

---

## MEDIUM

### M1. Missing transaction on `SaveEmotionalStateAsync` dual-write
**Where:** `SqliteMemoryService.cs:1302-1328`. Writes `emotional_state` (primary) + `emotional_state_history` (append) on the same connection but no transaction. A crash or cancel between the two leaves the history missing one record the dashboard will show as a state jump.
**Fix:** wrap in a transaction on one connection.

### M2. Per-call connection hammering on every operation
**Where:** every public method calls `OpenAsync`. For `RebuildMemoryLinksAsync` with N memories and the 50-window, you also open and close up to O(N) connections via `insertLink` — actually that's one connection per INSERT, all on the same outer connection, so fine. But `GetLinkedMemoriesAsync` (line 934-950) opens one connection then does N `CreateCommand` against it — no new connections per id — also fine. The pattern overall is acceptable but `SearchWithScoresAsync` link-enrichment (line 387-405) loops with one `linkCmd` per linked id on the same connection — each is a fresh compiled statement. A single `WHERE id IN (…)` would be ~10× faster at scale.
**Fix:** batch the linked-id lookup (after H1 is fixed for the IN-list construction).

### M3. `ContainsNovelSpecifics` false-positive on arithmetic-like content
**Where:** `SqliteMemoryService.cs:96` + `676-700`. `NumberPattern = \b\d+\b` matches *any* digit run. Merging "I have 2 cats" with "The cat is orange" — the merged "I have 2 orange cats" contains "2" which *is* in the sources — OK. But merging "Started running in 2023" with "Runs 5k regularly" into "Started running 5k in 2023" — both source numbers present. OK. But: "Mark texted: 'back from the gym'" merged with profile "Goes to gym weekly" — merged "Mark has been going to the gym weekly, back today" — may introduce date via LLM like "today's date 2026-04-18" from system context, which legitimately wasn't in either. This blocks valid merges. More likely: LLM outputs a word spelled-out-then-numeric ("5 kilometers" vs source "five km") — different tokens, looks novel.
**Impact:** over-rejection reduces merge rate; the merge path silently "fails" and reverts to insert-as-new (line 149), which *increases* duplicates. Feature 30's dedup gain is partially self-defeating.
**Fix:** widen source tokenization (number words → digits), or switch the gate from "any novel number" to "novel numbers **not** produced by reasonable transformations of source numbers." At minimum, log rejection reasons to confirm the false-positive rate isn't dominating.

### M4. `NamePattern` requires exactly two capitalized words, not one
**Where:** line 98. `@"\b[A-Z][a-z]+(?:\s[A-Z][a-z]+)\b"` — no quantifier on the non-capturing group, so "Mark" alone doesn't match but "Mark McArthey" does. Single-name false-positives ("Michigan", "Tuesday") sneak through when the source didn't contain them. Example: source 1 = "went to bed late", source 2 = "Mark said he's tired", merged = "Mark said he's tired from staying up late Tuesday" — "Tuesday" is a single-word capital, pattern doesn't fire, passes gate. This is a genuine confabulation the gate should catch.
**Fix:** make the group `(?:\s[A-Z][a-z]+)*` (zero-or-more) OR add a parallel `\b[A-Z][a-z]{3,}\b` pattern with a stop-word list (days, months, common capitalized non-names).

### M5. `FindMergeCandidateAsync` uses ORDER BY `occurred_at DESC LIMIT 50` — may miss older duplicates
**Where:** line 563-569. Comment says "no time window." But 50 rows of `InnerThought` cover perhaps 1–2 days at cycle cadence; anything older is unreachable as a merge target even if it's a near-perfect duplicate. Combined with H3 (merge keeps bumping `occurred_at`), the 50 newest become a mutually reinforcing cluster while older equivalents accumulate as never-dedup-eligible.
**Fix:** consider an `importance`-secondary sort or a separate "canonical profile" query for Semantic types. At minimum, update the stale comment — two contradictory doc comments live on this method (line 541-545 says "last N hours", line 546-551 says "no time window, last 50").

### M6. `GetRecentAuditEntriesAsync` uses string-interpolated `LIMIT`
**Where:** line 1585. `limit` is `int` so injection is impossible, but it breaks the parameterization convention every other method follows. Low risk; clean it up for consistency.

### M7. `Dispose` does not stop in-flight async operations
**Where:** line 73. `Dispose() => _keepAlive.Dispose();`. If a cognitive cycle is mid-save when the host shuts down, the per-call `OpenAsync` connection stays valid while `_keepAlive` is gone — for in-memory dbs this drops the whole database beneath the in-flight operation. For file dbs, benign. The test infrastructure uses in-memory dbs (connection string branch at line 47-56), so integration-test flakes possible.
**Fix:** implement `IAsyncDisposable`, track live operations with a counter or `CancellationTokenSource`, await drain before closing.

### M8. `TryCrossTypeProfileCorrectionAsync` and `FindMergeCandidateAsync` do not share threshold constants consistently
**Where:** cross-type uses hardcoded `0.85f` at line 753; `FindMergeCandidate` uses `MergeThreshold = 0.85f` constant at line 82. Same value, duplicated literal. The comment at 748-752 explains the history of 0.70→0.85. When (not if) you tune this again, one will drift.
**Fix:** reference `MergeThreshold` directly, or introduce a `CrossTypeMergeThreshold` constant and note why it may or may not diverge.

### M9. `SaveConfabulationFlagAsync` swallows no errors — INSERT without idempotency
**Where:** line 1211-1229. Unique constraint is just PK on `id = Guid.NewGuid()`, so you can't dedup identical user flags. If the same confabulation triggers two `///flag` commands in quick succession, you get two rows. Probably intentional ("count flags"), but not documented.

---

## LOW

### L1. `CREATE TABLE memories` does not include `provenance` column
**Where:** line 1853-1870. Provenance is added only via ALTER TABLE migration at 2051-2062. A fresh DB executes the CREATE TABLE first (no provenance), then the migration detects the missing column and adds it. Works, but future readers will wonder why the "authoritative" create statement doesn't match the shipping schema. Add `provenance TEXT NOT NULL DEFAULT 'Episodic'` to the CREATE TABLE.

### L2. `JsonSerializer.Deserialize<DesireState>(raw)` uses default options while `GetCharacterStateAsync` uses `JsonDefaults.CaseInsensitive`
**Where:** 1272 vs 1243. Inconsistent. Probably harmless if both wrote with the same serializer, but a future config change could bite.

### L3. `MigrationPath` does per-`PRAGMA table_info` scan 7 times
**Where:** 1982-2188. Each one opens its own reader over a full `PRAGMA table_info(memories)`. A single scan collecting all columns and a switch on name would be cleaner. Boot-time only; cosmetic.

### L4. `ReadContribution` has a bare `catch { }` swallowing all exceptions
**Where:** line 1702. Catches even unrelated errors (e.g., a thrown `OperationCanceledException` during read). CLAUDE.md "catch specific exceptions."

### L5. Migrations run every startup — idempotent but noisy
**Where:** 1840-2189. Each startup runs 7 PRAGMA checks + 0..N ALTERs. Idempotent, fine. But if a migration ever becomes expensive, you'll feel it. A simple `schema_version` table + guard would future-proof.

### L6. `SaveEmotionalContributionAsync` uses `INSERT OR REPLACE` — same class of concern as H4, lower impact because upsert is truly intended here.

### L7. No explicit `PRAGMA synchronous` / `busy_timeout` config. With WAL, the default `FULL` sync + 5s busy timeout are reasonable, but under singleton + high concurrency (voice + cognitive + inbound), a `busy_timeout=30000` would reduce transient `SQLITE_BUSY` under load.

---

## POSITIVE OBSERVATIONS

- WAL journal mode enabled correctly (`1848-1850`) — readers/writers don't block.
- `IOllamaClient` is optional (`_ollama?`) — search and save gracefully degrade if embedding is unavailable. Good defensive design.
- `ProvenanceBackfill` is a pure, testable static class with the heuristic isolated from IO — correctly structured.
- `SerialiseEmbedding`/`DeserialisedEmbedding` use `Buffer.BlockCopy` — correct and fast.
- Interface segregation (ISP) per `IMemoryService.cs:16` is well-executed; composite exists only for back-compat.
- `ConversationService.AddMessageAsync` admin-command guard (`192-196`) correctly writes to `conversation_messages` (needed for admin dispatch) while suppressing the Episodic memory save — fixes the pollution documented Apr 11.
- `Dispose()` keep-alive pattern for in-memory DBs is correctly used to survive per-call connection churn.

---

## SUGGESTED PRIORITY ORDER

1. **C1 (foreign_keys pragma)** — one-line fix, unlocks the defensive work you've already done.
2. **C3 (SaveAsync transaction/semaphore)** — prevents real dedup races in the singleton hot path.
3. **H3 (merge shouldn't bump occurred_at)** — directly undermines Feature 20 recency decay.
4. **H5 (audit log exception swallow)** — violates CLAUDE.md and guts your safety net.
5. **H2 (provenance-cross-tier contamination guard)** — epistemic grounding is the central architectural invariant.
6. **C2 / M5 docstring conflict** — either delete `ReassignMemoryLinksAsync` or wire it into `RebuildMemoryLinksAsync`.
7. The `NumberPattern`/`NamePattern` gates (M3/M4) should be exercised against real merge rejections in the log before tuning.

No tests were written or modified. No files were changed. Total effort to address C1+C3+H3+H5 is ~60 lines of changes plus 4–6 tests.
