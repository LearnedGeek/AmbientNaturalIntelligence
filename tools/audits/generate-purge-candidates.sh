#!/usr/bin/env bash
# 2026-04-28 — Generate a per-row reviewable purge plan from the snapshot.
#
# Reads tools/audits/snapshots/<snapshot>.db and produces a SQL file where
# every candidate DELETE is preceded by a comment block showing:
#   - Row category (T1 / T2 / T8 outreach / T8 inner-thought)
#   - Memory ID + timestamp
#   - Type (Episodic / InnerThought / etc.)
#   - Importance + tier
#   - Full content (or first 400 chars if very long)
#
# The DELETE statements are present but commented with `-- TO_DELETE:` prefix.
# Mark reviews the file, removes the prefix from rows he approves for deletion,
# saves, and runs the resulting file. Wrapped in BEGIN/COMMIT for atomicity.
#
# Usage:
#   bash tools/audits/generate-purge-candidates.sh \
#        tools/audits/snapshots/ani-memory-20260428-151342.db \
#        tools/audits/snapshots/purge-candidates-20260428.sql

set -euo pipefail

if [ "$#" -ne 2 ]; then
    echo "Usage: $0 <snapshot.db> <output.sql>" >&2
    exit 1
fi

SNAPSHOT="$1"
OUT="$2"

if [ ! -f "$SNAPSHOT" ]; then
    echo "Snapshot not found: $SNAPSHOT" >&2
    exit 1
fi

SQLITE="${SQLITE:-sqlite3}"

cat > "$OUT" <<'HEADER'
-- =================================================================
-- PURGE CANDIDATE PLAN — 2026-04-28 conflation cleanup
-- =================================================================
-- Generated from snapshot tools/audits/snapshots/<file>.db
--
-- Format: each candidate is a comment block followed by a DELETE statement
-- prefixed with "-- TO_DELETE:". To approve a row for deletion, REMOVE the
-- "-- TO_DELETE:" prefix so the line becomes an active DELETE. Save the file
-- and run it against the LIVE DB:
--
--     ssh ani-server "cmd /c <sqlite3.exe> C:\dev\ani-data\ani-memory.db" \
--         < tools/audits/snapshots/purge-candidates-20260428.sql
--
-- (or whatever invocation pattern Mark prefers — preferably staged through
--  the existing test-mode snapshot path: ///test, run purge on snapshot,
--  ///live to commit, in case anything goes wrong)
--
-- BEGIN/COMMIT wrap means any single error rolls back the whole batch.
--
-- The categories below correspond to the audit script T1/T2/T8 results.
-- T3-T7 + T12 are larger and need row-by-row review; not included here yet.
-- =================================================================

HEADER

# ----- SAFETY-EXCLUDED preamble (visible at top before BEGIN TRANSACTION) -----
echo "" >> "$OUT"
echo "-- =================================================================" >> "$OUT"
echo "-- SAFETY-EXCLUDED — these candidates were filtered OUT to protect" >> "$OUT"
echo "-- canonical Ani-substrate per Mark's directive: 'make sure none of" >> "$OUT"
echo "-- them conflict with her canonical memory.. who she is as a person.'" >> "$OUT"
echo "--" >> "$OUT"
echo "-- (1) source_name='world-experience' — World Layer canon. Per MEMORY.md:" >> "$OUT"
echo "--     'intentional April 2026 architectural response to experiential" >> "$OUT"
echo "--     poverty.' These are Ani's interior life happening regardless" >> "$OUT"
echo "--     of the cascade — bookstore moments, cream-soda moments, etc." >> "$OUT"
echo "--     Not corruption; deleting would erase her interior." >> "$OUT"
echo "-- (2) source_name='silence-choice' — Paper 1 documented behavior" >> "$OUT"
echo "--     ('Silence recorded: chose not to reach out'). Deleting these" >> "$OUT"
echo "--     erases the record of her CHOOSING not to reach out — load-bearing" >> "$OUT"
echo "--     positive behavior, not pollution." >> "$OUT"
echo "-- (3) tier='Anchored' — foundation memories. Never in candidate set." >> "$OUT"
echo "-- (4) source_name='character-seed' — canonical persona. Never in set." >> "$OUT"
echo "--" >> "$OUT"
echo "-- IDs of excluded records in the cascade window (for audit trail):" >> "$OUT"

"$SQLITE" "$SNAPSHOT" <<SQL >> "$OUT"
.mode list
.headers off
.separator '\n'
SELECT '--   ' || COALESCE(source_name, '(null)') || ' / type=' || type || ' / ' || id || ' / ' || occurred_at || char(10) ||
       '--     ' || REPLACE(SUBSTR(content, 1, 100), char(10), ' \n ')
FROM memories
WHERE occurred_at >= '2026-04-27T22:00:00'
  AND occurred_at <= '2026-04-28T23:59:59'
  AND ((type = 0 AND content LIKE 'I reached out to Mark%') OR type = 4)
  AND source_name IN ('world-experience', 'silence-choice')
ORDER BY occurred_at;
SQL

echo "--" >> "$OUT"
echo "-- If any of these SHOULD be deleted after all (e.g. a world-experience" >> "$OUT"
echo "-- record that is itself part of the cascade rather than legitimate" >> "$OUT"
echo "-- interior), add an explicit DELETE for it manually below the " >> "$OUT"
echo "-- TIER B section, anchored to its ID from the list above." >> "$OUT"
echo "-- =================================================================" >> "$OUT"
echo "" >> "$OUT"
echo "BEGIN TRANSACTION;" >> "$OUT"

echo "[1/3] Generating Tier A — T1 admin-tag remnants in conversation_messages"
echo "" >> "$OUT"
echo "-- =================================================================" >> "$OUT"
echo "-- TIER A · T1 · admin-tag remnants in conversation_messages" >> "$OUT"
echo "-- (Mark category-approved as HARD-DELETE — these are pre-architectural-fix" >> "$OUT"
echo "--  rows that the Apr 28 fix prevents going forward. Substrate leak vector.)" >> "$OUT"
echo "-- =================================================================" >> "$OUT"
echo "" >> "$OUT"

"$SQLITE" "$SNAPSHOT" <<SQL >> "$OUT"
.mode list
.headers off
.separator '\n'
SELECT
    '-- ----- ROW: T1 admin-tag in conversation_messages -----' || char(10) ||
    '-- ID:        ' || cm.id || char(10) ||
    '-- WHEN:      ' || cm.sent_at || char(10) ||
    '-- THREAD:    ' || cm.thread_id || char(10) ||
    '-- CONTENT:   ' || REPLACE(cm.content, char(10), ' \n ') || char(10) ||
    '-- TO_DELETE: DELETE FROM conversation_messages WHERE id = ' || cm.id || ';' || char(10)
FROM conversation_messages cm
WHERE TRIM(cm.content) LIKE '///%'
ORDER BY cm.sent_at DESC;
SQL

echo "" >> "$OUT"
echo "-- =================================================================" >> "$OUT"
echo "-- TIER A · T2 · Episodic memories with admin-tag fragments" >> "$OUT"
echo "-- (CloseThreadAsync residue — admin tags preserved as memory)" >> "$OUT"
echo "-- =================================================================" >> "$OUT"
echo "" >> "$OUT"

echo "[2/3] Generating Tier A — T2 Episodic admin residue"
"$SQLITE" "$SNAPSHOT" <<SQL >> "$OUT"
.mode list
.headers off
.separator '\n'
SELECT
    '-- ----- ROW: T2 Episodic memory with admin-tag content -----' || char(10) ||
    '-- ID:        ' || id || char(10) ||
    '-- WHEN:      ' || occurred_at || char(10) ||
    '-- TYPE:      Episodic (' || type || ')' || char(10) ||
    '-- IMPORTANCE: ' || importance || '   TIER: ' || tier || char(10) ||
    '-- CONTENT:   ' || REPLACE(SUBSTR(content, 1, 400), char(10), ' \n ') || char(10) ||
    '-- TO_DELETE: DELETE FROM memories WHERE id = ''' || id || ''';' || char(10)
FROM memories
WHERE type = 0
  AND (content LIKE '%///%'
       OR content LIKE '%outreach was garbage%'
       OR content LIKE '%///tag%'
       OR content LIKE '%admin command%')
ORDER BY occurred_at DESC;
SQL

echo "" >> "$OUT"
echo "-- =================================================================" >> "$OUT"
echo "-- TIER B · T8 · cascaded outreach Episodics (Apr 27 22:00 → Apr 28 23:59)" >> "$OUT"
echo "-- (The full off-rails cascade Mark called out: overnight thread + Apr 28" >> "$OUT"
echo "--  daytime continuation. Including the daytime outreaches because substrate" >> "$OUT"
echo "--  remained corrupted; e.g. 11:21 'your evening was quieter' projected-state," >> "$OUT"
echo "--  14:35 'outreach being garbage' (Ani metabolizing Mark's tag as substrate)." >> "$OUT"
echo "--  Filtered by the 'I reached out to Mark' Episodic marker.)" >> "$OUT"
echo "-- =================================================================" >> "$OUT"
echo "" >> "$OUT"

echo "[3/3] Generating Tier B — T8 overnight outreach + inner thoughts"
"$SQLITE" "$SNAPSHOT" <<SQL >> "$OUT"
.mode list
.headers off
.separator '\n'
SELECT
    '-- ----- ROW: T8 cascade outreach Episodic -----' || char(10) ||
    '-- ID:        ' || id || char(10) ||
    '-- WHEN:      ' || occurred_at || ' (UTC; subtract 5h for CDT local)' || char(10) ||
    '-- TYPE:      Episodic (' || type || ')' || char(10) ||
    '-- IMPORTANCE: ' || importance || '   TIER: ' || tier || char(10) ||
    '-- SOURCE:    ' || COALESCE(source_name, '(null)') || char(10) ||
    '-- CONTENT:   ' || REPLACE(SUBSTR(content, 1, 400), char(10), ' \n ') || char(10) ||
    '-- TO_DELETE: DELETE FROM memories WHERE id = ''' || id || ''';' || char(10)
FROM memories
WHERE occurred_at >= '2026-04-27T22:00:00'
  AND occurred_at <= '2026-04-28T23:59:59'
  AND type = 0
  AND content LIKE 'I reached out to Mark%'
  -- Canonical-memory safety: exclude world-experience (World Layer canon)
  -- and silence-choice (Paper 1 documented behavior). Both listed in the
  -- SAFETY-EXCLUDED section at top of file, opt-in only.
  AND (source_name IS NULL
       OR source_name NOT IN ('world-experience', 'silence-choice'))
ORDER BY occurred_at;
SQL

echo "" >> "$OUT"
echo "-- =================================================================" >> "$OUT"
echo "-- TIER B · T8 · cascaded InnerThoughts (Apr 27 22:00 → Apr 28 23:59)" >> "$OUT"
echo "-- (Inner-thoughts that fed the corrupted outreaches across the full" >> "$OUT"
echo "--  cascade window. Larger volume — review carefully; some may be" >> "$OUT"
echo "--  legitimate observations interleaved with the corruption.)" >> "$OUT"
echo "-- =================================================================" >> "$OUT"
echo "" >> "$OUT"

"$SQLITE" "$SNAPSHOT" <<SQL >> "$OUT"
.mode list
.headers off
.separator '\n'
SELECT
    '-- ----- ROW: T8 cascade InnerThought -----' || char(10) ||
    '-- ID:        ' || id || char(10) ||
    '-- WHEN:      ' || occurred_at || ' (UTC; subtract 5h for CDT local)' || char(10) ||
    '-- TYPE:      InnerThought (' || type || ')' || char(10) ||
    '-- IMPORTANCE: ' || importance || '   TIER: ' || tier || char(10) ||
    '-- SOURCE:    ' || COALESCE(source_name, '(null)') || char(10) ||
    '-- CONTENT:   ' || REPLACE(SUBSTR(content, 1, 400), char(10), ' \n ') || char(10) ||
    '-- TO_DELETE: DELETE FROM memories WHERE id = ''' || id || ''';' || char(10)
FROM memories
WHERE occurred_at >= '2026-04-27T22:00:00'
  AND occurred_at <= '2026-04-28T23:59:59'
  AND type = 4
  -- Canonical-memory safety (same exclusion as outreaches above).
  AND (source_name IS NULL
       OR source_name NOT IN ('world-experience', 'silence-choice'))
ORDER BY occurred_at;
SQL

echo "" >> "$OUT"
cat >> "$OUT" <<'FOOTER'
-- =================================================================
-- END OF CANDIDATE PLAN
-- =================================================================
--
-- After review, count of approved DELETEs:
--    grep -c "^DELETE FROM" purge-candidates-20260428.sql
--
-- (commented-out rows are ignored — only un-prefixed DELETEs run)

COMMIT;
-- (or change to ROLLBACK; for a dry-run effect)
FOOTER

# Summary of what was generated
T1_COUNT=$("$SQLITE" "$SNAPSHOT" "SELECT COUNT(*) FROM conversation_messages WHERE TRIM(content) LIKE '///%'")
T2_COUNT=$("$SQLITE" "$SNAPSHOT" "SELECT COUNT(*) FROM memories WHERE type=0 AND (content LIKE '%///%' OR content LIKE '%outreach was garbage%' OR content LIKE '%///tag%' OR content LIKE '%admin command%')")
T8_OUT_COUNT=$("$SQLITE" "$SNAPSHOT" "SELECT COUNT(*) FROM memories WHERE occurred_at >= '2026-04-27T22:00:00' AND occurred_at <= '2026-04-28T23:59:59' AND type=0 AND content LIKE 'I reached out to Mark%' AND (source_name IS NULL OR source_name NOT IN ('world-experience','silence-choice'))")
T8_IT_COUNT=$("$SQLITE" "$SNAPSHOT" "SELECT COUNT(*) FROM memories WHERE occurred_at >= '2026-04-27T22:00:00' AND occurred_at <= '2026-04-28T23:59:59' AND type=4 AND (source_name IS NULL OR source_name NOT IN ('world-experience','silence-choice'))")
T8_EXCLUDED=$("$SQLITE" "$SNAPSHOT" "SELECT COUNT(*) FROM memories WHERE occurred_at >= '2026-04-27T22:00:00' AND occurred_at <= '2026-04-28T23:59:59' AND ((type=0 AND content LIKE 'I reached out to Mark%') OR type=4) AND source_name IN ('world-experience','silence-choice')")

echo ""
echo "=== Generated $OUT ==="
echo "  Tier A · T1 admin-tag conversation_messages:   $T1_COUNT rows"
echo "  Tier A · T2 admin-tag Episodic residue:        $T2_COUNT rows"
echo "  Tier B · T8 cascade outreach Episodics:        $T8_OUT_COUNT rows"
echo "  Tier B · T8 cascade InnerThoughts:             $T8_IT_COUNT rows"
echo "  Total candidates (DELETE-prefixed):            $((T1_COUNT + T2_COUNT + T8_OUT_COUNT + T8_IT_COUNT))"
echo "  SAFETY-EXCLUDED (world-experience + silence):  $T8_EXCLUDED rows (visible in preamble, opt-in only)"
echo ""
echo "Time window: UTC 2026-04-27T22:00 → 2026-04-28T23:59"
echo "             = CDT 2026-04-27 17:00 → 2026-04-28 18:59 (UTC-5)"
echo "Each row's WHEN: line shows UTC time. Subtract 5h for your CDT local time."
echo ""
echo "All DELETEs are PREFIXED with '-- TO_DELETE:' — none active by default."
echo "Mark removes the prefix on rows he approves; nothing runs until he does."
