-- Issue #43 — C.2 effectiveness matrix prototype query.
--
-- Computes per-cell (mark_register_dominant × ani_register_dominant)
-- statistics: count, mean outcome_signal_valence, mirror-vs-cross axis.
-- Source: closed_conversation_records, validity = 'valid' only
-- (excludes the 28 quarantined fabrication records from J.5h-data).
--
-- 2026-05-22 update (Issue #51 — Layer 3 tie-breaker): the original
-- `ORDER BY value DESC LIMIT 1` dominant-register lookup silently biased
-- 50/50 tied cells by JSON storage order, hiding Desire records behind
-- Tenderness. This version computes a global register frequency and uses
-- it as a tie-breaker — on ties, the less-frequent register wins, so
-- minority signal surfaces rather than hides.
--
-- Run-as: snapshot the prod DB first
--   ssh ani-server "powershell -Command \"& 'C:/Users/cortexadmin/sqlite3.exe' \
--     'C:/dev/ani-data/ani-memory.db' '.backup C:/tmp/ani-memory-c2.db'\""
--   scp ani-server:C:/tmp/ani-memory-c2.db e:/tmp/ani-memory-c2.db
--   sqlite3 e:/tmp/ani-memory-c2.db < scripts/analytics/2026-05-21-c2-effectiveness-matrix.sql
--
-- See Issue #43 comment dated 2026-05-21 for first-pass findings.
-- See Issue #51 for the tie-breaker rationale.

.mode column
.headers on

-- 0. Substrate health.
SELECT 'total_valid_records' AS metric, COUNT(*) AS n
FROM closed_conversation_records
WHERE validity = 'valid';

-- Reusable: global register frequency across BOTH mark + ani register
-- vectors. Used as the tie-breaker for "dominant register" lookups.
DROP VIEW IF EXISTS c2_global_register_freq;
CREATE TEMP VIEW c2_global_register_freq AS
WITH all_regs AS (
    SELECT key AS reg
    FROM closed_conversation_records ccr, json_each(ccr.mark_register_json)
    WHERE ccr.validity = 'valid' AND value > 0
    UNION ALL
    SELECT key
    FROM closed_conversation_records ccr, json_each(ccr.ani_register_json)
    WHERE ccr.validity = 'valid' AND value > 0
)
SELECT reg, COUNT(*) AS freq
FROM all_regs
GROUP BY reg;

-- Tie-broken dominant register: ORDER BY (-value, +freq) so the less-
-- frequent register wins on ties. Captures both turn weight AND
-- minority-signal preservation.
DROP VIEW IF EXISTS c2_dom;
CREATE TEMP VIEW c2_dom AS
SELECT
    ccr.id,
    (SELECT je.key
     FROM json_each(ccr.mark_register_json) je
     LEFT JOIN c2_global_register_freq cf ON cf.reg = je.key
     WHERE je.value > 0
     ORDER BY je.value DESC, COALESCE(cf.freq, 0) ASC
     LIMIT 1) AS mark_dom,
    (SELECT je.key
     FROM json_each(ccr.ani_register_json) je
     LEFT JOIN c2_global_register_freq cf ON cf.reg = je.key
     WHERE je.value > 0
     ORDER BY je.value DESC, COALESCE(cf.freq, 0) ASC
     LIMIT 1) AS ani_dom,
    ccr.outcome_signal_valence
FROM closed_conversation_records ccr
WHERE ccr.validity = 'valid';

-- 1. Per-cell effectiveness matrix.
SELECT
    mark_dom                                       AS mark_register,
    ani_dom                                        AS ani_register,
    CASE WHEN mark_dom = ani_dom THEN 'mirror' ELSE 'cross' END AS axis,
    COUNT(*)                                       AS n,
    ROUND(AVG(outcome_signal_valence), 3)          AS avg_valence
FROM c2_dom
WHERE mark_dom IS NOT NULL AND ani_dom IS NOT NULL
GROUP BY mark_dom, ani_dom
ORDER BY n DESC, avg_valence DESC;

-- 2. Mirroring vs cross outcome delta per Mark register.
SELECT '--- mirror vs cross per Mark register ---' AS section;
SELECT
    mark_dom                                       AS mark_register,
    CASE WHEN mark_dom = ani_dom THEN 'mirror' ELSE 'cross' END AS axis,
    COUNT(*)                                       AS n,
    ROUND(AVG(outcome_signal_valence), 3)          AS avg_valence
FROM c2_dom
WHERE mark_dom IS NOT NULL AND ani_dom IS NOT NULL
GROUP BY mark_dom, axis
ORDER BY mark_dom, axis;

-- 3. Aggregate mirror vs cross — the headline number.
SELECT '--- AGGREGATE mirror vs cross ---' AS section;
SELECT
    CASE WHEN mark_dom = ani_dom THEN 'mirror' ELSE 'cross' END AS axis,
    COUNT(*)                                       AS n,
    ROUND(AVG(outcome_signal_valence), 3)          AS avg_valence
FROM c2_dom
WHERE mark_dom IS NOT NULL AND ani_dom IS NOT NULL
GROUP BY axis
ORDER BY axis;

-- 4. Tied-record transparency (#51 acceptance). Count records where the
-- top two register values are equal on either side — these are the
-- records the tie-breaker is making the call on.
SELECT '--- TIED RECORDS (#51 transparency) ---' AS section;

WITH side_ties AS (
    SELECT
        ccr.id,
        (SELECT MAX(value) FROM json_each(ccr.mark_register_json)) AS mark_max,
        (SELECT COUNT(*) FROM json_each(ccr.mark_register_json) WHERE value = (SELECT MAX(value) FROM json_each(ccr.mark_register_json))) AS mark_at_max,
        (SELECT MAX(value) FROM json_each(ccr.ani_register_json))  AS ani_max,
        (SELECT COUNT(*) FROM json_each(ccr.ani_register_json) WHERE value = (SELECT MAX(value) FROM json_each(ccr.ani_register_json)))  AS ani_at_max
    FROM closed_conversation_records ccr
    WHERE ccr.validity = 'valid'
)
SELECT
    SUM(CASE WHEN mark_at_max >= 2 THEN 1 ELSE 0 END) AS records_with_mark_tie,
    SUM(CASE WHEN ani_at_max  >= 2 THEN 1 ELSE 0 END) AS records_with_ani_tie,
    SUM(CASE WHEN mark_at_max >= 2 OR ani_at_max >= 2 THEN 1 ELSE 0 END) AS records_with_any_tie
FROM side_ties;

-- 5. Sample-size feasibility per cell.
SELECT '--- sample-size buckets per cell ---' AS section;

WITH cells AS (
    SELECT mark_dom, ani_dom, COUNT(*) AS n
    FROM c2_dom WHERE mark_dom IS NOT NULL AND ani_dom IS NOT NULL
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

-- 6. Global register frequency (sanity check on the tie-breaker).
SELECT '--- global register frequency (tie-breaker ranking) ---' AS section;
SELECT reg, freq FROM c2_global_register_freq ORDER BY freq DESC;
