# ANI Data-Layer Refactor — Phase 1 Inventory

**Generated:** 2026-05-17 evening
**Purpose:** Pre-refactor inventory of all raw-SQLite code in the main ani-memory DB. Drives entity scaffold + DbContext design in subsequent Phase 1 steps.

## Scope Expansion Notice

Initial refactor plan named only `SqliteMemoryService`. Actual scope includes three services all sharing the same physical DB (`ani-memory.db`):

| Service | Lines | Tables Owned |
|---|---|---|
| `SqliteMemoryService` | ~2,000 | memories, character_state, desire_state, emotional_state, relationship_health, emotional_state_history, memory_contradictions, emotional_contributions, confabulation_flags, memory_links, memory_audit |
| `SqliteConversationService` | medium | conversation_threads, conversation_messages |
| `SqliteClosedConversationStore` | small | closed_conversation_records |

**EmergenceStore is OUT of scope** — uses a separate physical DB (`ani-emergence.db`). Can be refactored later as a smaller follow-on.

All three main-DB services need to share **one** `AniDbContext` so atomic composition across them is possible (e.g., saving a conversation message + linking related memories in one transaction).

## Tables (Main DB)

### memories
Columns: id (TEXT PK), type (INT), content (TEXT), raw_json (TEXT), importance (REAL), relational_valence (REAL), embedding (BLOB), is_resolved (INT), source_name (TEXT), occurred_at (TEXT), created_at (TEXT), resolved_at (TEXT), tier (TEXT default 'Standard'), anchor_reason (TEXT), anchored_at (TEXT), **provenance (TEXT default 'Episodic')**

Indices: ix_memories_type, ix_memories_occurred

### character_state, desire_state, emotional_state, relationship_health
JSON-blob singleton tables. `id INTEGER PRIMARY KEY, json TEXT NOT NULL`

### emotional_state_history
id (autoinc), warmth, energy, concern, playfulness, contact_gap_tension, recorded_at. Index: ix_emotional_history_time

### memory_contradictions
new_memory_id + existing_memory_id (composite PK), reason, similarity, flagged_at, is_resolved

### emotional_contributions
id (TEXT PK), source_content, warmth_delta, energy_delta, concern_delta, playfulness_delta, created_at, half_life_hours, category, embedding (BLOB), severity, is_outreach_ready, **register**, **ml_emotion**, **ml_confidence**, **ml_sarcasm**, **divergence_score**, **associative_anchor**. Index: ix_contributions_created

### confabulation_flags
id (TEXT PK), flagged_at, contact_message, ani_reply, topic_category, notes, **canonical_category**. Index: ix_confab_flags_time

### memory_links
source_id + target_id + relationship (composite PK), created_at. FK both to memories(id). Indices: ix_memory_links_source, ix_memory_links_target

### memory_audit
id (autoinc), memory_id, action, source, content_before, content_after, type_before, type_after, importance_before, importance_after, occurred_at. Indices: ix_audit_memory, ix_audit_time, ix_audit_action

### conversation_threads
(SqliteConversationService — schema details to be captured in Phase 1.4)

### conversation_messages
(SqliteConversationService — schema details to be captured in Phase 1.4)

### closed_conversation_records
(SqliteClosedConversationStore — schema details to be captured in Phase 1.4)

## Multi-Operation Flows Requiring Atomicity

These are the flows where the current architecture fails because each method opens its own transaction. After EF + UoW, these become single-transaction operations.

1. **`SaveAsync(memory)` + dedup/merge + audit + link creation** — already partially atomic within SaveAsync but composes audit + link creation that can fail independently
2. **`SaveReflectionGistAndCompressAsync` (NEW for Phase 3)** — save gist + mark sources Compressed + create compressed_into links. Today's bug source.
3. **`MergeMemoriesAsync` + contradicts-link creation + audit** — merge updates one record, creates contradicts link, writes audit. All-or-nothing.
4. **`AnchorMemoryAsync` + audit** — anchoring is a state change that needs auditing in same transaction
5. **`SaveEmotionalContributionAsync` + emotional_state update** — adding a contribution should atomically update the rolling emotional_state snapshot
6. **`ResolveOpenLoopAsync` + memory update** — resolution updates resolved flag and resolved_at
7. **Closed conversation finalization** — when a thread closes, multiple writes happen across conversation_threads + closed_conversation_records + possibly memory_links

## Method Inventory (Public API surface to preserve)

44 public methods on `SqliteMemoryService` grouped by aggregate:

**MemoryRepository (memory CRUD + search):**
- SaveAsync, DeleteAsync, AdjustImportanceAsync, AnchorMemoryAsync
- GetByTypeAsync, GetRecentAsync, GetDecayEligibleAsync, GetByTierAsync, GetAnchoredMemoriesAsync
- SearchAsync, SearchWithScoresAsync, SearchByTypeAsync, SearchByTierAsync
- MarkCompressedAsync

**MemoryLinkRepository:**
- GetLinkedMemoriesAsync, GetAllLinksAsync, GetLinkCountAsync
- RebuildMemoryLinksAsync (utility)

**CharacterStateRepository:**
- GetCharacterStateAsync, SaveCharacterStateAsync

**EmotionalStateRepository:**
- GetEmotionalStateAsync, SaveEmotionalStateAsync, GetEmotionalHistoryAsync

**DesireStateRepository:**
- GetDesireStateAsync, SaveDesireStateAsync

**RelationshipHealthRepository:**
- GetRelationshipHealthAsync, SaveRelationshipHealthAsync

**EmotionalContributionRepository:**
- SaveEmotionalContributionAsync, GetActiveContributionsAsync, GetContributionsSinceAsync
- CleanupDecayedContributionsAsync, ExpireContributionAsync
- GetProcessedThemesAsync

**OpenLoopRepository:**
- GetOpenLoopsAsync, ResolveOpenLoopAsync

**ConfabulationFlagRepository:**
- SaveConfabulationFlagAsync, GetFlaggedContradictionsAsync, ResolveContradictionAsync

**AuditRepository:**
- GetRecentAuditEntriesAsync, RestoreFromAuditAsync

**Stats/Analytics (could be a query-only repository):**
- GetRecentMessageCountAsync, GetAverageConversationValenceAsync, GetInitiativeBalanceAsync

## Service Behaviors That Must Survive

1. **Connection pooling pattern** — `_keepAlive` connection holds the in-memory shared-cache DB open
2. **Auto-embedding via injected IOllamaClient** in SaveAsync
3. **Rumination guard** (Apr 21 anti-cluster-saturation check on InnerThought saves)
4. **Feature 30 three-tier dedup/merge** (>0.95 skip / 0.85-0.95 merge / <0.85 insert)
5. **Feature 31 auto-link creation** on save (relates_to links to recent same-type records)
6. **Audit logging** on every create/update/delete/merge
7. **Save serialization** via `_saveLock` semaphore (prevent concurrent dedup races at service layer)

These all become explicit operations within the EF-based service layer; nothing gets silently dropped.

## Risks Surfaced During Inventory

- **Hand-written ALTER TABLE migrations** at startup (anchor_reason, anchored_at, provenance, contact_gap_tension, severity, is_outreach_ready, register, ml_emotion, ml_confidence, ml_sarcasm, divergence_score, associative_anchor, canonical_category, tier). EF initial migration must match the cumulative end state, not the original schema.
- **mark_valence → relational_valence column rename** in production — confirm production DB already has the new column name (it does, per migration code that ran at some point).
- **memories.id is TEXT** but `MemoryRecord.Id` is `Guid` — EF property converter needed.
- **Embedding BLOB is float[]** serialized as bytes — needs custom value converter.
- **EpistemicTier enum** stored as TEXT in `provenance` column — string conversion.
- **DecayTier enum** stored as TEXT in `tier` column — string conversion.

## Phase 1 Deliverables Remaining

- Add EF Core packages to AniRuntime.Memory project + Service project (for DI registration)
- Create AniDbContext + entity classes
- Generate initial migration matching cumulative schema (DB-first scaffolding likely cleanest)
- Verify schema diff vs production snapshot

Then Phase 2 begins.
