-- Issue #56 — collapse duplicate clusters in `memories` table.
--
-- Background: the cosine merge policy (EfMemoryMergePolicy.FindMergeCandidateAsync)
-- scans only the last 50 records of the same type, which can miss older seeds
-- buried behind newer writes. Production has multi-copy clusters of identical
-- character-seed Semantic records — e.g. the Mia seed exists 5 times.
--
-- The companion code change (commit on this branch) adds
-- IMemoryMergePolicy.FindExactContentDuplicateAsync, a full-table exact-content
-- check that runs BEFORE the cosine merge tier. That stops future duplicate
-- inserts. This script collapses the existing clusters.
--
-- Strategy: for each (type, content, source_name) cluster with > 1 row,
-- keep the row with the highest importance (oldest created_at as tie-breaker)
-- and delete the rest. Preserves the most "trusted" copy and the original
-- birth-stamp.
--
-- Idempotent: re-running with no duplicates produces 0 deletions.
-- Reversible only by pre-run backup. Take one:
--   sqlite3 ani-memory.db ".backup ani-memory-pre-issue56-cleanup-20260522.db"
--
-- Pre-run snapshot: C:\dev\ani-data\backups\ani-memory-pre-issue56-cleanup-20260522.db (TODO)

-- ── Step 1: report cluster shape BEFORE cleanup ─────────────────────────────

SELECT 'duplicate_clusters_before' AS label, COUNT(*) AS n
FROM (
    SELECT type, content, source_name
    FROM memories
    WHERE type IN (1, 2, 3, 4)  -- Semantic, OpenLoop, Commitment, InnerThought (DedupableTypes; enum order)
    GROUP BY type, content, source_name
    HAVING COUNT(*) > 1
);

SELECT 'duplicate_rows_to_delete' AS label, SUM(cnt - 1) AS n
FROM (
    SELECT COUNT(*) AS cnt
    FROM memories
    WHERE type IN (1, 5, 6, 7)
    GROUP BY type, content, source_name
    HAVING COUNT(*) > 1
);

-- Top 10 largest clusters for visibility:
SELECT type, source_name, COUNT(*) AS copies,
       MIN(importance) AS min_imp, MAX(importance) AS max_imp,
       SUBSTR(content, 1, 60) AS content_preview
FROM memories
WHERE type IN (1, 5, 6, 7)
GROUP BY type, content, source_name
HAVING COUNT(*) > 1
ORDER BY copies DESC, type, source_name
LIMIT 10;

-- ── Step 2: delete duplicates, keeping highest-importance / oldest copy ─────

DELETE FROM memories
WHERE id IN (
    SELECT m.id
    FROM memories m
    JOIN (
        SELECT type, content, source_name,
               -- Identify the "survivor" for each cluster: highest importance,
               -- oldest created_at as tie-breaker. Cast importance to REAL to
               -- guarantee numeric ordering even if SQLite reads it as text.
               (SELECT id FROM memories m2
                WHERE m2.type = m.type
                  AND m2.content = m.content
                  AND (m2.source_name = m.source_name OR (m2.source_name IS NULL AND m.source_name IS NULL))
                ORDER BY CAST(m2.importance AS REAL) DESC, m2.created_at ASC
                LIMIT 1) AS survivor_id
        FROM memories m
        WHERE m.type IN (1, 5, 6, 7)
        GROUP BY m.type, m.content, m.source_name
        HAVING COUNT(*) > 1
    ) survivors
        ON survivors.type = m.type
       AND survivors.content = m.content
       AND (survivors.source_name = m.source_name OR (survivors.source_name IS NULL AND m.source_name IS NULL))
    WHERE m.id != survivors.survivor_id
      AND m.type IN (1, 5, 6, 7)
);

-- ── Step 3: report cluster shape AFTER cleanup ──────────────────────────────

SELECT 'duplicate_clusters_after' AS label, COUNT(*) AS n
FROM (
    SELECT type, content, source_name
    FROM memories
    WHERE type IN (1, 5, 6, 7)
    GROUP BY type, content, source_name
    HAVING COUNT(*) > 1
);

SELECT 'total_memories_after' AS label, COUNT(*) AS n FROM memories;

-- Specific verification for the Mia case from #56:
SELECT 'mia_seed_copies_after' AS label, COUNT(*) AS n
FROM memories
WHERE source_name = 'character-seed'
  AND content LIKE '%Mia%15-year-old daughter%';
