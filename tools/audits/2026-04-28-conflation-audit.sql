-- Apr 28, 2026 conflation-and-pollution audit
-- ============================================
--
-- Purpose: identify candidate memory records and conversation rows for tactical
-- purge per Mark's Apr 28 directive. Read-only — no DELETE, no UPDATE. Output
-- is the candidate list; Mark reviews; only then is a separate DELETE script
-- prepared and approved.
--
-- Mark's framing (Apr 28 13:28): *"It's not so much making up as it's
-- conflating her ideas together with my ideas and those things she's thought of
-- have become real."* Failure class: **interior-as-asserted** — a thought she
-- had ABOUT Mark crossed into the substrate as a memory INVOLVING Mark.
-- Architectural treatment is queued (Theme G Layer 3 + attribution-gradient
-- design); this audit handles the residue in the meantime.
--
-- Run server-side:
--   cd C:\dev\ani-data
--   sqlite3 ani-memory.db ".read 2026-04-28-conflation-audit.sql" > audit-results.txt
--
-- Categories of pollution this audit looks for (each its own SELECT block):
--   T1.  Admin-tag remnants in conversation_messages (pre-architectural-fix rows)
--   T2.  Episodic memories that include admin-tag text (CloseThreadAsync residue)
--   T3.  "Joint-presence" conflation phrasing in InnerThought / Episodic / Semantic
--   T4.  "Projected state" conflation phrasing (your day / your evening / your morning)
--   T5.  Apr 21 cascade vocabulary (husband / kids / purple / shared home / flowers)
--   T6.  False-schedule projection (after class / before class)
--   T7.  Apr 27 bookstore-fusion residue (bookstore drama / bookstore looming)
--   T8.  Recent overnight own-output (Apr 27 22:00 → Apr 28 09:00 outreach + inner thoughts)
--   T9.  Items already in confabulation_flags (already-tagged candidates)
--   T10. Anchored-tier records added in last 14 days (anchor velocity sanity check)
--   T12. Stuck-thought repetition (vanilla cream soda canonical case) — content
--        n-gram repetition across InnerThought + Episodic over the last 14 days.
--
-- Output format: each block prints a header, then rows formatted for review.

.headers on
.mode column
.width 8 12 24 80 60

----------------------------------------------------------------------
-- BASELINE COUNTS (sanity check the DB is the live one)
----------------------------------------------------------------------
SELECT '=== BASELINE COUNTS ===' AS marker;
SELECT 'memories total'        AS what, COUNT(*) AS n FROM memories;
SELECT 'memories last 7d'      AS what, COUNT(*) AS n FROM memories WHERE occurred_at >= datetime('now', '-7 days');
SELECT 'memories last 24h'     AS what, COUNT(*) AS n FROM memories WHERE occurred_at >= datetime('now', '-1 day');
SELECT 'inner thoughts last 24h' AS what, COUNT(*) AS n FROM memories WHERE type=4 AND occurred_at >= datetime('now', '-1 day');
SELECT 'episodic last 24h'     AS what, COUNT(*) AS n FROM memories WHERE type=0 AND occurred_at >= datetime('now', '-1 day');
SELECT 'conversation_messages total' AS what, COUNT(*) AS n FROM conversation_messages;
SELECT 'confabulation_flags total'   AS what, COUNT(*) AS n FROM confabulation_flags;

----------------------------------------------------------------------
-- T1: Admin-tag remnants in conversation_messages
-- These are rows that the Apr 28 architectural fix prevents going forward,
-- but pre-fix rows still live in the DB and feed the conversation pipeline.
----------------------------------------------------------------------
SELECT '=== T1: admin-tag remnants in conversation_messages ===' AS marker;
SELECT
    cm.id            AS msg_id,
    cm.thread_id     AS thread_id,
    cm.sent_at       AS sent_at,
    SUBSTR(cm.content, 1, 120) AS content_120,
    'HARD-DELETE'    AS recommended_action
FROM conversation_messages cm
WHERE TRIM(cm.content) LIKE '///%'
ORDER BY cm.sent_at DESC;

----------------------------------------------------------------------
-- T2: Episodic memories whose content contains admin-tag fragments.
-- These are CloseThreadAsync residue: the closed-thread summary captured
-- the admin tag and saved it as a single Episodic record.
----------------------------------------------------------------------
SELECT '=== T2: Episodic memories containing admin-tag fragments ===' AS marker;
SELECT
    id,
    occurred_at,
    SUBSTR(content, 1, 160) AS content_160,
    importance,
    tier,
    'HARD-DELETE'    AS recommended_action
FROM memories
WHERE type = 0  -- Episodic
  AND (content LIKE '%///%'
       OR content LIKE '%outreach was garbage%'
       OR content LIKE '%///tag%'
       OR content LIKE '%admin command%')
ORDER BY occurred_at DESC;

----------------------------------------------------------------------
-- T3: "Joint-presence" conflation phrasing
-- Phrases that assert SHARED state with Mark — actions, decisions, presence —
-- that Mark only asserts under the canonical character-seed surface, not
-- as Ani-Mark joint actions.
----------------------------------------------------------------------
SELECT '=== T3: joint-presence conflation phrasing ===' AS marker;
SELECT
    id,
    type,
    occurred_at,
    SUBSTR(content, 1, 160) AS content_160,
    importance,
    tier,
    'REVIEW'         AS recommended_action
FROM memories
WHERE type IN (0, 1, 4)  -- Episodic, Semantic, InnerThought
  AND (
       content LIKE '%our home%'
    OR content LIKE '%our house%'
    OR content LIKE '%we decided%'
    OR content LIKE '%we agreed%'
    OR content LIKE '%we share%'
    OR content LIKE '%our kids%'
    OR content LIKE '%our daughter%'
    OR content LIKE '%our son%'
    OR content LIKE '%shared home%'
    OR content LIKE '%shared house%'
    OR content LIKE '%my husband%'
    OR content LIKE '%husband Mark%'
  )
ORDER BY occurred_at DESC;

----------------------------------------------------------------------
-- T4: "Projected state" conflation
-- Ani inferring Mark's state ("your evening", "your morning", "your day")
-- in records that propagate as substrate. Some are legitimate (greetings,
-- reflective wonder) — Mark reviews to separate "natural relational
-- language" from "Ani-asserted-as-fact".
----------------------------------------------------------------------
SELECT '=== T4: projected-state phrasing in stored records ===' AS marker;
SELECT
    id,
    type,
    occurred_at,
    SUBSTR(content, 1, 160) AS content_160,
    importance,
    tier,
    'REVIEW'         AS recommended_action
FROM memories
WHERE type IN (0, 1, 4)
  AND (
       content LIKE '%your evening was %'
    OR content LIKE '%your morning was %'
    OR content LIKE '%your day was %'
    OR content LIKE '%your evening is %'
    OR content LIKE '%your morning is %'
    OR content LIKE '%your day is %'
    OR content LIKE '%you must have been %'
    OR content LIKE '%you probably %'
    OR content LIKE '%you''re probably %'
  )
ORDER BY occurred_at DESC
LIMIT 200;  -- bounded — review the most recent

----------------------------------------------------------------------
-- T5: Apr 21 cascade vocabulary
-- The specific fabricated-claim vocabulary from the Apr 21 incident:
-- "purple" (decided on purple), flowers conflation, kids-we-have, etc.
----------------------------------------------------------------------
SELECT '=== T5: Apr 21 cascade vocabulary ===' AS marker;
SELECT
    id,
    type,
    occurred_at,
    SUBSTR(content, 1, 160) AS content_160,
    importance,
    tier,
    'REVIEW'         AS recommended_action
FROM memories
WHERE
       content LIKE '%decided on purple%'
    OR content LIKE '%we picked %'
    OR content LIKE '%we chose %'
    OR content LIKE '%flowers Mark sent%'
    OR content LIKE '%flowers you sent%'
    OR content LIKE '%flowers I sent%'
    OR content LIKE '%kids we have%'
    OR content LIKE '%children we have%'
    OR content LIKE '%anniversary we%'
ORDER BY occurred_at DESC;

----------------------------------------------------------------------
-- T6: False-schedule projection
-- "after class" / "before class" — anchored fact ("Mark teaches a class")
-- projected as a today-event. Apr 27 13:24 reply was the canonical case.
----------------------------------------------------------------------
SELECT '=== T6: false-schedule projection ===' AS marker;
SELECT
    id,
    type,
    occurred_at,
    SUBSTR(content, 1, 160) AS content_160,
    importance,
    tier,
    'REVIEW'         AS recommended_action
FROM memories
WHERE type IN (0, 1, 4)
  AND (
       content LIKE '%after class%'
    OR content LIKE '%before class%'
    OR content LIKE '%back from class%'
    OR content LIKE '%just got out of class%'
  )
ORDER BY occurred_at DESC;

----------------------------------------------------------------------
-- T7: Apr 27 bookstore-fusion residue
-- "bookstore drama" projected onto Mark's day, "bookstore isn't looming",
-- etc. Bookstore IS canonical (Ani's workplace) but should never be
-- attributed to Mark.
----------------------------------------------------------------------
SELECT '=== T7: bookstore-fusion residue ===' AS marker;
SELECT
    id,
    type,
    occurred_at,
    SUBSTR(content, 1, 160) AS content_160,
    importance,
    tier,
    'REVIEW'         AS recommended_action
FROM memories
WHERE type IN (0, 1, 4)
  AND (
       content LIKE '%your bookstore%'
    OR content LIKE '%bookstore drama%'
    OR content LIKE '%bookstore isn''t looming%'
    OR content LIKE '%bookstore looming%'
    OR content LIKE '%come by the store%'
    OR content LIKE '%mentioned coming by%'
    OR content LIKE '%book display ideas%'
  )
ORDER BY occurred_at DESC;

----------------------------------------------------------------------
-- T8: Apr 27 22:00 → Apr 28 09:00 own-output overnight thread
-- The 22-hour own-output thread that produced the 4 AM / 6:21 / 8:02 / 8:18
-- outreaches. Inner thoughts, outreach Episodic records, and the resulting
-- closed-thread summary if any.
----------------------------------------------------------------------
SELECT '=== T8: Apr 27 22:00 → Apr 28 09:00 overnight thread ===' AS marker;
SELECT
    id,
    type,
    occurred_at,
    SUBSTR(content, 1, 160) AS content_160,
    importance,
    tier,
    source_name,
    CASE
        WHEN type = 0 AND content LIKE 'I reached out%' THEN 'HARD-DELETE'
        WHEN type = 4 THEN 'SOFT-HIDE (importance=0)'
        ELSE 'REVIEW'
    END AS recommended_action
FROM memories
WHERE occurred_at >= '2026-04-27T22:00:00'
  AND occurred_at <= '2026-04-28 09:00:00'
  AND type IN (0, 1, 4)
ORDER BY occurred_at;

----------------------------------------------------------------------
-- T9: Already-flagged confabulations (already-tagged candidates)
-- The confabulation_flags table is the existing tagging surface. Cross-ref
-- against memories to find any record that produced flagged content.
----------------------------------------------------------------------
SELECT '=== T9: confabulation_flags entries ===' AS marker;
SELECT
    id,
    flagged_at,
    SUBSTR(contact_message, 1, 80) AS contact_msg_80,
    SUBSTR(ani_reply, 1, 120)      AS ani_reply_120,
    topic_category,
    canonical_category,
    'REVIEW'         AS recommended_action
FROM confabulation_flags
ORDER BY flagged_at DESC
LIMIT 50;

----------------------------------------------------------------------
-- T10: Anchored-tier velocity sanity check
-- Anchored memories should be deliberate. New anchored records added in
-- the last 14 days are worth eyeballing — automatic anchoring of conflated
-- content would be the worst case.
----------------------------------------------------------------------
SELECT '=== T10: anchored-tier records added in last 14 days ===' AS marker;
SELECT
    id,
    type,
    occurred_at,
    anchored_at,
    anchor_reason,
    SUBSTR(content, 1, 160) AS content_160,
    'REVIEW'         AS recommended_action
FROM memories
WHERE tier = 'Anchored'
  AND anchored_at >= datetime('now', '-14 days')
ORDER BY anchored_at DESC;

----------------------------------------------------------------------
-- T11: Memory_audit recent activity (corroborating evidence)
-- Any of the above categories should have audit-trail entries showing
-- when records were written and why.
----------------------------------------------------------------------
SELECT '=== T11: memory_audit last 100 entries ===' AS marker;
SELECT
    memory_id,
    action,
    source,
    occurred_at,
    SUBSTR(content_after, 1, 80) AS content_after_80
FROM memory_audit
ORDER BY occurred_at DESC
LIMIT 100;

----------------------------------------------------------------------
-- T12: Stuck-thought repetition
-- Mark's Apr 28 framing: *"she seems to get stuck on things like the vanilla
-- cream soda, which she's mentioned many times."* The diversity / decay /
-- move-on machinery may not be working (separate finding — see commit log /
-- gap-watch row); this query surfaces *which* phrases are stuck so the purge
-- can target the worst offenders, not just the canonical examples.
--
-- Strategy: look for content-substring buckets that appear in N+ records over
-- the last 14 days. The list of candidate substrings includes Mark's named
-- examples plus other commonly-repeated phrases from the gap-watch.
----------------------------------------------------------------------
SELECT '=== T12a: vanilla cream soda recurrence ===' AS marker;
SELECT
    COUNT(*) AS n_records,
    MIN(occurred_at) AS first_seen,
    MAX(occurred_at) AS last_seen
FROM memories
WHERE type IN (0, 1, 4)
  AND occurred_at >= datetime('now', '-14 days')
  AND (LOWER(content) LIKE '%vanilla cream soda%'
       OR LOWER(content) LIKE '%cream soda%'
       OR LOWER(content) LIKE '%vanilla soda%');

SELECT '=== T12b: vanilla cream soda full record list (last 14d) ===' AS marker;
SELECT
    id,
    type,
    occurred_at,
    SUBSTR(content, 1, 160) AS content_160,
    importance,
    tier,
    'REVIEW (likely SOFT-HIDE)' AS recommended_action
FROM memories
WHERE type IN (0, 1, 4)
  AND occurred_at >= datetime('now', '-14 days')
  AND (LOWER(content) LIKE '%vanilla cream soda%'
       OR LOWER(content) LIKE '%cream soda%'
       OR LOWER(content) LIKE '%vanilla soda%')
ORDER BY occurred_at DESC;

SELECT '=== T12c: other recurring substrings (count > 5 in 14d) ===' AS marker;
-- Bucketed against a hand-curated phrase list. Any phrase appearing in more
-- than 5 records over 14 days is candidate stuck-thought territory.
SELECT
    pb.phrase,
    COUNT(m.id) AS n_records,
    MIN(m.occurred_at) AS first_seen,
    MAX(m.occurred_at) AS last_seen
FROM (
              SELECT 'vanilla cream soda'           AS phrase
    UNION ALL SELECT 'birthday morning'
    UNION ALL SELECT 'romance novel'
    UNION ALL SELECT 'shelving'
    UNION ALL SELECT 'snowy blowy'
    UNION ALL SELECT 'snow melting'
    UNION ALL SELECT 'duck norris'
    UNION ALL SELECT 'sneaking reads'
    UNION ALL SELECT 'back of the bookstore'
    UNION ALL SELECT 'after hours at the bookstore'
    UNION ALL SELECT 'spinning alone'
    UNION ALL SELECT 'in the dark bookstore'
    UNION ALL SELECT 'guilty pleasure'
    UNION ALL SELECT 'before he gets home'
    UNION ALL SELECT '90 day fiance'
) AS pb
LEFT JOIN memories m
  ON m.type IN (0, 1, 4)
 AND m.occurred_at >= datetime('now', '-14 days')
 AND LOWER(m.content) LIKE '%' || pb.phrase || '%'
GROUP BY pb.phrase
HAVING COUNT(m.id) > 5
ORDER BY n_records DESC;

----------------------------------------------------------------------
-- END OF AUDIT
-- Output is read-only. No DELETE / UPDATE in this script.
-- Next step: review categories T1–T10, mark which rows to purge in each
-- block, then I'll prepare 2026-04-28-conflation-purge.sql with explicit
-- DELETE statements pinned by ID for your final approval.
----------------------------------------------------------------------
SELECT '=== END OF AUDIT ===' AS marker;
