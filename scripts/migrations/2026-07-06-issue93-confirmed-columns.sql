-- Issue #93 — Add `confirmed_at` + `confirmed_by` columns to memories, and
-- retroactively backfill Facts + Episodic records with their birth-stamp
-- confirmation. Interior / world-experience / reflection stay NULL.
--
-- Companion to the code change at:
--   - src/AniRuntime.Memory/Entities/MemoryEntity.cs (ConfirmedAt, ConfirmedBy)
--   - src/AniRuntime.Core/Models/MemoryRecord.cs (ConfirmedAt, ConfirmedBy)
--   - src/AniRuntime.Memory/AniDbContext.cs (converter + index)
--   - src/AniRuntime.Memory/EfMemoryPersistenceService.cs (map on save)
--   - src/AniRuntime.Memory/EfSemanticSearchComposer.cs (composite-score bias)
--   - src/AniRuntime.Core/AniOptions.cs (RetrievalConfirmationBoost)
--
-- Design (from the substrate-vs-scaffold post-mortem + Mark's directive
-- 2026-07-06): the graph is poisoned because Interior records inherit the
-- name-cluster from a single character-seed and Ani's inner-thought
-- generator laundered them into world-experience over months. Retrieval
-- scores confirmed content and unconfirmed content on the same axis, so
-- 101 Interior "Kevin" records with high importance beat 14 Episodic
-- Kevin records that actually happened. This migration retroactively
-- applies the substrate-correction design: Facts + Episodic are the
-- reality pool, Interior is the dream pool, retrieval biases toward
-- reality without hard-excluding the dream pool.
--
-- Pre-run snapshot (run on ani-server before applying):
--   sqlite3 C:/dev/ani-data/ani-memory.db ".backup C:/dev/ani-data/backups/ani-memory-pre-issue93-confirmed-20260706.db"

-- ── Step 1: Add confirmed_at column (TEXT for ISO-8601 nullable DateTimeOffset) ─
ALTER TABLE memories ADD COLUMN confirmed_at TEXT NULL;

-- ── Step 2: Add confirmed_by column (TEXT, canonical values "canonical" / "mark-tag") ─
ALTER TABLE memories ADD COLUMN confirmed_by TEXT NULL;

-- ── Step 3: Index for the IS NOT NULL check used by the retrieval bias ────────
CREATE INDEX IF NOT EXISTS ix_memories_confirmed_at ON memories (confirmed_at);

-- ── Step 4: Pre-backfill distribution snapshot ────────────────────────────────
SELECT 'pre_backfill' AS label, provenance, COUNT(*) AS n,
       SUM(CASE WHEN confirmed_at IS NOT NULL THEN 1 ELSE 0 END) AS confirmed
FROM memories
GROUP BY provenance
ORDER BY provenance;

-- ── Step 5: Retroactive backfill — Facts + Episodic get birth-stamp confirmation ─
--
-- Facts: character seeds, twilio-inbound, perception, RSS, calendar, contact-state,
--   Mark-side conversation. These ARE reality — Mark asserted them or the world
--   emitted them. ConfirmedAt = CreatedAt (the moment they entered the substrate)
--   because their entry is the confirmation event.
--
-- Episodic: verbatim conversation record — Ani-side turn events, outreach dispatches.
--   These ARE reality in the sense that they factually happened as conversational
--   events, even though their content is Ani's own composition. Confirmed = the
--   event occurred.
--
-- Interior + world-experience + reflection: stay NULL. These are dreams /
--   interpretations / self-templating. They can be promoted to confirmed later
--   via Mark ///tag → LLM classifier → ConfirmedBy='mark-tag'.
UPDATE memories
SET confirmed_at = created_at,
    confirmed_by = 'canonical'
WHERE provenance IN ('Facts', 'Episodic')
  AND confirmed_at IS NULL;

-- ── Step 6: Post-backfill audit ───────────────────────────────────────────────
SELECT 'post_backfill' AS label, provenance, COUNT(*) AS n,
       SUM(CASE WHEN confirmed_at IS NOT NULL THEN 1 ELSE 0 END) AS confirmed,
       SUM(CASE WHEN confirmed_by = 'canonical' THEN 1 ELSE 0 END) AS canonical
FROM memories
GROUP BY provenance
ORDER BY provenance;

-- Invariant checks — expressed as SELECTs; migration runner should assert
-- (1) Facts + Episodic totals equal their confirmed totals
-- (2) Interior + Reflection + WorldExperience confirmed totals are 0
SELECT 'invariant_facts_all_confirmed' AS label,
       CASE WHEN COUNT(*) = SUM(CASE WHEN confirmed_at IS NOT NULL THEN 1 ELSE 0 END)
            THEN 'pass' ELSE 'FAIL' END AS result
FROM memories WHERE provenance = 'Facts';

SELECT 'invariant_episodic_all_confirmed' AS label,
       CASE WHEN COUNT(*) = SUM(CASE WHEN confirmed_at IS NOT NULL THEN 1 ELSE 0 END)
            THEN 'pass' ELSE 'FAIL' END AS result
FROM memories WHERE provenance = 'Episodic';

SELECT 'invariant_interior_none_confirmed' AS label,
       CASE WHEN SUM(CASE WHEN confirmed_at IS NOT NULL THEN 1 ELSE 0 END) = 0
            THEN 'pass' ELSE 'FAIL' END AS result
FROM memories WHERE provenance = 'Interior';
