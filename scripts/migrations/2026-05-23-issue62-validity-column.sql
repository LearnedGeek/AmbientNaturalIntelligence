-- Issue #62 — Add `validity` column to memories table.
--
-- Companion to the code change at:
--   - src/AniRuntime.Memory/Entities/MemoryEntity.cs (Validity property)
--   - src/AniRuntime.Memory/Repositories/MemoryRepository.cs (filter on validity = 'valid')
--   - src/AniRuntime.Memory/EfMemoryPersistenceService.cs (MarkConversationTurnInvalidAsync)
--   - src/AniRuntime.Loops/Admin/Commands/TagCommand.cs (wire walk-back to invalidation)
--
-- The new column defaults to 'valid' so existing records keep current behavior.
-- The new code (TagCommand walk-back, GetForSemanticSearchAsync filter) takes effect
-- only once this migration has been applied AND the deploy has shipped.
--
-- Schema strategy mirrors the J.5h pattern on closed_conversation_records (see
-- src/AniRuntime.Memory/SqliteClosedConversationStore.cs:91-113): explicit ALTER TABLE
-- + indexed for the equality filter the retrieval path uses.
--
-- Idempotent under partial-migration replay: SQLite's ALTER TABLE ADD COLUMN
-- fails if the column exists, so we attempt then ignore the error via wrapping
-- in a savepoint at the application level (or by running each statement
-- separately and ignoring known "duplicate column name" errors).
--
-- Pre-run snapshot (run on ani-server before applying):
--   sqlite3 C:/dev/ani-data/ani-memory.db ".backup C:/dev/ani-data/backups/ani-memory-pre-issue62-validity-20260523.db"

-- ── Step 1: Add validity column (default 'valid') ───────────────────────────
ALTER TABLE memories ADD COLUMN validity TEXT NOT NULL DEFAULT 'valid';

-- ── Step 2: Index for the equality filter used by GetForSemanticSearchAsync ─
CREATE INDEX IF NOT EXISTS ix_memories_validity ON memories (validity);

-- ── Step 3: Verification ───────────────────────────────────────────────────
SELECT 'columns_after' AS label, name, type, dflt_value
FROM pragma_table_info('memories')
WHERE name = 'validity';

SELECT 'distribution' AS label, validity, COUNT(*) AS n
FROM memories
GROUP BY validity;
