-- =============================================================================
-- Migration: 2026-05-07-tier-separation.sql
-- Purpose:   Replace legacy `memories.tier` (always 'Standard') with the locked
--            EpistemicTier enum {Facts, Episodic, Interior} per the
--            Tier Interface Contract 1-Pager (May 7, 2026).
--
--            Per Mark's May 7 20:45 CDT decision: column is renamed to
--            `epistemic_tier` (not kept as `tier`) for research clarity —
--            future readers of the schema see the EpistemicTier intent
--            without needing to read the contract.
--
-- Source mapping (from contract §2, locked May 7):
--   twilio-inbound, character-seed, rss, weather              -> Facts
--   conversation w/ content "Mark said:" / "Mark texted:"     -> Facts
--   conversation w/ content "I said to Mark:" / "I reached..."-> Episodic
--   silence-choice                                            -> Episodic
--   world-experience, reflection, temporal-gap                -> Interior
--   NULL source_name                                          -> Interior  (locked default)
--   anything remaining (incl. mixed conversation summaries)   -> Interior  (defensive default)
--
-- Strategy (table-recreate idiom; the SQLite-recommended pattern for
-- changing column constraints + dropping columns):
--   1. Detect schema state. If already migrated, abort cleanly via a CHECK
--      violation on a temp sentinel (transaction rolls back, no harm done).
--   2. Build memories_new with the canonical final shape (legacy `tier`
--      removed, new `tier` NOT NULL CHECK in {Facts,Episodic,Interior}).
--   3. INSERT ... SELECT, computing the new tier inline via a CASE per §2.
--      The CHECK constraint catches any row that fails to map.
--   4. Verify row count matches via a CHECK-trapped sentinel.
--   5. DROP old memories, RENAME memories_new -> memories, recreate indexes.
--
-- Idempotent: re-running on an already-migrated DB is a controlled no-op.
-- Atomic:    everything is wrapped in a single transaction. Any failure
--            rolls back to the pre-migration state.
-- Scope:     `memories` table only. memory_links / memory_audit untouched.
--
-- IMPORTANT: Run only against the migration test DB
--            (/e/tmp/ani-memory-migration-test.db). Do NOT run against the
--            production DB on ani-server without backup + scheduled change.
-- =============================================================================

PRAGMA foreign_keys = OFF;

BEGIN;

-- ---------------------------------------------------------------------------
-- Step 1: idempotency guard.
--
-- We detect "already migrated" as: `epistemic_tier` exists as a column on
-- `memories` (i.e. the rename has happened), every row has a value in
-- {Facts,Episodic,Interior}, and the legacy `tier` column is gone. If that
-- holds, we insert a value that violates a CHECK constraint, which forces
-- this transaction to ROLLBACK with a clear message — no destructive ops
-- have run yet at this point.
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE _migration_guard (
    proceed INTEGER NOT NULL
        CHECK (proceed = 1) -- abort transaction if 0 (already migrated)
);

INSERT INTO _migration_guard (proceed)
SELECT CASE
    WHEN (SELECT COUNT(*) FROM pragma_table_info('memories')
          WHERE name = 'epistemic_tier') = 1
    THEN 0  -- already migrated; CHECK fires, txn aborts cleanly
    ELSE 1  -- proceed with migration (legacy `tier` column still present)
END;

-- If we reach here, the guard accepted (proceed=1). Drop it; not needed.
DROP TABLE _migration_guard;

-- ---------------------------------------------------------------------------
-- Step 2: build the canonical post-migration memories table.
-- ---------------------------------------------------------------------------
CREATE TABLE memories_new (
    id                  TEXT PRIMARY KEY,
    type                INTEGER NOT NULL,
    content             TEXT    NOT NULL,
    raw_json            TEXT,
    importance          REAL    NOT NULL DEFAULT 0,
    relational_valence  REAL    NOT NULL DEFAULT 0,
    embedding           BLOB,
    is_resolved         INTEGER NOT NULL DEFAULT 0,
    source_name         TEXT,
    occurred_at         TEXT    NOT NULL,
    created_at          TEXT    NOT NULL,
    resolved_at         TEXT,
    epistemic_tier      TEXT    NOT NULL
                                CHECK (epistemic_tier IN ('Facts','Episodic','Interior')),
    anchor_reason       TEXT,
    anchored_at         TEXT,
    provenance          TEXT    NOT NULL DEFAULT 'Episodic'
);

-- ---------------------------------------------------------------------------
-- Step 3: copy + classify in one INSERT...SELECT. Precedence is encoded by
-- CASE branch order. Final ELSE is the defensive Interior default per
-- Mark's locked May 7 decision.
-- ---------------------------------------------------------------------------
INSERT INTO memories_new (
    id, type, content, raw_json, importance, relational_valence, embedding,
    is_resolved, source_name, occurred_at, created_at, resolved_at,
    epistemic_tier, anchor_reason, anchored_at, provenance
)
SELECT
    id, type, content, raw_json, importance, relational_valence, embedding,
    is_resolved, source_name, occurred_at, created_at, resolved_at,
    CASE
        -- Facts: Mark-asserted or external-world sources
        WHEN source_name IN ('twilio-inbound', 'character-seed', 'rss', 'weather')
            THEN 'Facts'
        WHEN source_name = 'conversation'
            AND (content LIKE 'Mark said:%' OR content LIKE 'Mark texted:%')
            THEN 'Facts'

        -- Episodic: Ani-side events, things Ani said/did
        WHEN source_name = 'conversation'
            AND (content LIKE 'I said to Mark:%' OR content LIKE 'I reached out to Mark:%')
            THEN 'Episodic'
        WHEN source_name = 'silence-choice'
            THEN 'Episodic'

        -- Interior: Ani's inner content
        WHEN source_name IN ('world-experience', 'reflection', 'temporal-gap')
            THEN 'Interior'

        -- Defensive default: NULL source_name and any remaining
        -- (e.g. mixed conversation summary records that don't match the
        -- four canonical prefixes) map to Interior. Locked May 7:
        -- default-Interior at migration, review after.
        ELSE 'Interior'
    END,
    anchor_reason, anchored_at, provenance
FROM memories;

-- ---------------------------------------------------------------------------
-- Step 4: row-count sanity check. If memories_new and memories disagree, we
-- abort the txn via the same CHECK-violation pattern.
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE _row_count_guard (
    ok INTEGER NOT NULL CHECK (ok = 1)
);

INSERT INTO _row_count_guard (ok)
SELECT CASE
    WHEN (SELECT COUNT(*) FROM memories_new) = (SELECT COUNT(*) FROM memories)
    THEN 1
    ELSE 0  -- mismatch; abort
END;

DROP TABLE _row_count_guard;

-- ---------------------------------------------------------------------------
-- Step 5: tier-population sanity check. Every row must have a non-empty tier
-- in the allowed set. The CHECK constraint already enforces this at INSERT
-- time, but we re-verify defensively.
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE _tier_pop_guard (
    ok INTEGER NOT NULL CHECK (ok = 1)
);

INSERT INTO _tier_pop_guard (ok)
SELECT CASE
    WHEN (SELECT COUNT(*) FROM memories_new
          WHERE epistemic_tier IS NULL
             OR epistemic_tier = ''
             OR epistemic_tier NOT IN ('Facts','Episodic','Interior')) = 0
    THEN 1
    ELSE 0
END;

DROP TABLE _tier_pop_guard;

-- ---------------------------------------------------------------------------
-- Step 6: swap tables and recreate indexes.
-- ---------------------------------------------------------------------------
DROP TABLE memories;
ALTER TABLE memories_new RENAME TO memories;

CREATE INDEX IF NOT EXISTS ix_memories_type            ON memories (type);
CREATE INDEX IF NOT EXISTS ix_memories_occurred        ON memories (occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_memories_epistemic_tier  ON memories (epistemic_tier);

COMMIT;

PRAGMA foreign_keys = ON;

-- =============================================================================
-- End of migration. Run the companion verify script to confirm tier
-- distribution and sample records.
-- =============================================================================
