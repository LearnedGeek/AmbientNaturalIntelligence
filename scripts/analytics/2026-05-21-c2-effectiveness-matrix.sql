-- Issue #43 — C.2 effectiveness matrix prototype query.
--
-- Computes per-cell (mark_register_dominant × ani_register_dominant)
-- statistics: count, mean outcome_signal_valence, mirror-vs-cross axis.
-- Source: closed_conversation_records, validity = 'valid' only
-- (excludes the 28 quarantined fabrication records from J.5h-data).
--
-- Run-as: snapshot the prod DB first
--   ssh ani-server "powershell -Command \"& 'C:/Users/cortexadmin/sqlite3.exe' \
--     'C:/dev/ani-data/ani-memory.db' '.backup C:/tmp/ani-memory-c2.db'\""
--   scp ani-server:C:/tmp/ani-memory-c2.db e:/tmp/ani-memory-c2.db
--   sqlite3 e:/tmp/ani-memory-c2.db < scripts/analytics/2026-05-21-c2-effectiveness-matrix.sql
--
-- See Issue #43 comment dated 2026-05-21 for first-pass findings:
-- 0.15-point cross-vs-mirror gap (cross wins) at N=66; Playfulness row
-- carries the strongest mirror-vs-cross signal in the data
-- (mirror −0.125 vs cross +0.560, gap 0.685).
--
-- No new schema. Computes everything from existing columns; the matrix
-- is a read-only view over the producer-shipped substrate.

.mode column
.headers on

-- 0. Substrate health.
SELECT 'total_valid_records' AS metric, COUNT(*) AS n
FROM closed_conversation_records
WHERE validity = 'valid';

-- 1. Per-cell effectiveness matrix.
WITH dom AS (
    SELECT
        ccr.id,
        (SELECT key FROM json_each(ccr.mark_register_json) WHERE value > 0 ORDER BY value DESC LIMIT 1) AS mark_dom,
        (SELECT key FROM json_each(ccr.ani_register_json)  WHERE value > 0 ORDER BY value DESC LIMIT 1) AS ani_dom,
        ccr.outcome_signal_valence
    FROM closed_conversation_records ccr
    WHERE ccr.validity = 'valid'
)
SELECT
    mark_dom                                       AS mark_register,
    ani_dom                                        AS ani_register,
    CASE WHEN mark_dom = ani_dom THEN 'mirror' ELSE 'cross' END AS axis,
    COUNT(*)                                       AS n,
    ROUND(AVG(outcome_signal_valence), 3)          AS avg_valence
FROM dom
WHERE mark_dom IS NOT NULL AND ani_dom IS NOT NULL
GROUP BY mark_dom, ani_dom
ORDER BY n DESC, avg_valence DESC;

-- 2. Mirroring vs cross outcome delta per Mark register.
SELECT '--- mirror vs cross per Mark register ---' AS section;

WITH dom AS (
    SELECT
        (SELECT key FROM json_each(ccr.mark_register_json) WHERE value > 0 ORDER BY value DESC LIMIT 1) AS mark_dom,
        (SELECT key FROM json_each(ccr.ani_register_json)  WHERE value > 0 ORDER BY value DESC LIMIT 1) AS ani_dom,
        ccr.outcome_signal_valence
    FROM closed_conversation_records ccr
    WHERE ccr.validity = 'valid'
)
SELECT
    mark_dom                                       AS mark_register,
    CASE WHEN mark_dom = ani_dom THEN 'mirror' ELSE 'cross' END AS axis,
    COUNT(*)                                       AS n,
    ROUND(AVG(outcome_signal_valence), 3)          AS avg_valence
FROM dom
WHERE mark_dom IS NOT NULL AND ani_dom IS NOT NULL
GROUP BY mark_dom, axis
ORDER BY mark_dom, axis;

-- 3. Aggregate mirror vs cross — the headline number.
SELECT '--- AGGREGATE mirror vs cross ---' AS section;

WITH dom AS (
    SELECT
        (SELECT key FROM json_each(ccr.mark_register_json) WHERE value > 0 ORDER BY value DESC LIMIT 1) AS mark_dom,
        (SELECT key FROM json_each(ccr.ani_register_json)  WHERE value > 0 ORDER BY value DESC LIMIT 1) AS ani_dom,
        ccr.outcome_signal_valence
    FROM closed_conversation_records ccr
    WHERE ccr.validity = 'valid'
)
SELECT
    CASE WHEN mark_dom = ani_dom THEN 'mirror' ELSE 'cross' END AS axis,
    COUNT(*)                                       AS n,
    ROUND(AVG(outcome_signal_valence), 3)          AS avg_valence
FROM dom
WHERE mark_dom IS NOT NULL AND ani_dom IS NOT NULL
GROUP BY axis
ORDER BY axis;

-- 4. Sample-size feasibility per cell.
SELECT '--- sample-size buckets per cell ---' AS section;

WITH dom AS (
    SELECT
        (SELECT key FROM json_each(ccr.mark_register_json) WHERE value > 0 ORDER BY value DESC LIMIT 1) AS mark_dom,
        (SELECT key FROM json_each(ccr.ani_register_json)  WHERE value > 0 ORDER BY value DESC LIMIT 1) AS ani_dom
    FROM closed_conversation_records ccr
    WHERE ccr.validity = 'valid'
),
cells AS (
    SELECT mark_dom, ani_dom, COUNT(*) AS n
    FROM dom WHERE mark_dom IS NOT NULL AND ani_dom IS NOT NULL
    GROUP BY mark_dom, ani_dom
)
SELECT
    CASE
        WHEN n >= 10 THEN 'A: >= 10  (paper-figure ready)'
        WHEN n >= 5  THEN 'B: 5-9    (provisional)'
        WHEN n >= 2  THEN 'C: 2-4    (sparse)'
        ELSE              'D: 1      (singleton)'
    END AS bucket,
    COUNT(*) AS num_cells,
    SUM(n)   AS records_in_bucket
FROM cells
GROUP BY bucket
ORDER BY bucket;
