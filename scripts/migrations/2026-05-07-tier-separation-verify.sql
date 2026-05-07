-- =============================================================================
-- Verify: 2026-05-07-tier-separation-verify.sql
-- Purpose: post-migration sanity report.
-- Usage:   sqlite3 <db> < scripts/migrations/2026-05-07-tier-separation-verify.sql
--
-- Reports:
--   1. Schema shape of `memories` (column list, types, NOT NULL flags).
--   2. Row counts per resulting tier.
--   3. Three sample rows per tier (id, source_name, content snippet, tier).
--   4. Count of rows with NULL or invalid tier (must be 0).
--   5. Total row count (for cross-check vs pre-migration count).
--   6. Source-name x tier crosstab (helps Mark spot any unexpected mappings).
-- =============================================================================

.headers on
.mode column
.width 22 8 6 4

SELECT '=== 1. SCHEMA SHAPE ===' AS section;
SELECT cid, name, type, "notnull", dflt_value
FROM pragma_table_info('memories')
ORDER BY cid;

SELECT '=== 2. ROW COUNTS PER TIER ===' AS section;
SELECT tier, COUNT(*) AS n
FROM memories
GROUP BY tier
ORDER BY tier;

SELECT '=== 3a. SAMPLE: Facts (3 rows) ===' AS section;
SELECT
    substr(id, 1, 8)               AS id_prefix,
    COALESCE(source_name, '<NULL>') AS source_name,
    substr(content, 1, 70)         AS content_snippet,
    tier
FROM memories
WHERE tier = 'Facts'
ORDER BY occurred_at DESC
LIMIT 3;

SELECT '=== 3b. SAMPLE: Episodic (3 rows) ===' AS section;
SELECT
    substr(id, 1, 8)               AS id_prefix,
    COALESCE(source_name, '<NULL>') AS source_name,
    substr(content, 1, 70)         AS content_snippet,
    tier
FROM memories
WHERE tier = 'Episodic'
ORDER BY occurred_at DESC
LIMIT 3;

SELECT '=== 3c. SAMPLE: Interior (3 rows) ===' AS section;
SELECT
    substr(id, 1, 8)               AS id_prefix,
    COALESCE(source_name, '<NULL>') AS source_name,
    substr(content, 1, 70)         AS content_snippet,
    tier
FROM memories
WHERE tier = 'Interior'
ORDER BY occurred_at DESC
LIMIT 3;

SELECT '=== 4. NULL / INVALID TIER CHECK (must be 0) ===' AS section;
SELECT
    COUNT(*) AS n_bad
FROM memories
WHERE tier IS NULL
   OR tier = ''
   OR tier NOT IN ('Facts','Episodic','Interior');

SELECT '=== 5. TOTAL ROWS ===' AS section;
SELECT COUNT(*) AS total_rows FROM memories;

SELECT '=== 6. SOURCE x TIER CROSSTAB ===' AS section;
SELECT
    COALESCE(source_name, '<NULL>') AS source_name,
    SUM(CASE WHEN tier = 'Facts'    THEN 1 ELSE 0 END) AS facts,
    SUM(CASE WHEN tier = 'Episodic' THEN 1 ELSE 0 END) AS episodic,
    SUM(CASE WHEN tier = 'Interior' THEN 1 ELSE 0 END) AS interior,
    COUNT(*)                                           AS total
FROM memories
GROUP BY source_name
ORDER BY total DESC;
