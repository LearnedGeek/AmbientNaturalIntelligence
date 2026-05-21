-- Issue #47 J.5h-data Step 1 — quarantine fabricated closed-conversation
-- records (executed against production 2026-05-21).
--
-- Background: `ClosedConversationSummarizer` (added Apr 29 V1.2) was never
-- migrated through Theme J's CognitiveOutputGate (J.5a–g enumeration
-- pre-dated its existence). When given a one-sided thread (only Ani turns,
-- typical unanswered outreach), the summarizer prompt's "between Mark and
-- Ani" framing causes the LLM to fabricate Mark-side narration.
--
-- Empirical scope on 2026-05-21: 28 such records since 2026-05-14. See
-- `docs/research/ANI-Research-Log.md` 2026-05-21 entry for the audit.
--
-- This script flips their `validity` to 'invalid_fabrication' so substrate
-- retrieval (V1.5 bias, outreach grounding, default GetByIdAsync /
-- GetByThreadIdAsync / GetRecentAsync) filters them out. Records are
-- preserved for audit / paper-figure work via the new IncludingInvalid
-- audit-path methods.
--
-- Idempotent: re-running has no effect on records already marked invalid.
-- Reversible: UPDATE ... SET validity = 'valid' WHERE id IN (...).
--
-- Pre-run snapshot: C:\dev\ani-data\backups\ani-memory-pre-j5h-quarantine-20260521.db.

UPDATE closed_conversation_records
SET validity = 'invalid_fabrication'
WHERE id IN (
    SELECT ccr.id
    FROM closed_conversation_records ccr
    JOIN (
        SELECT thread_id,
               SUM(CASE WHEN role = 'mark' THEN 1 ELSE 0 END) AS mark_turns,
               SUM(CASE WHEN role = 'ani'  THEN 1 ELSE 0 END) AS ani_turns
        FROM conversation_messages
        GROUP BY thread_id
    ) ts ON ts.thread_id = ccr.thread_id
    WHERE ts.mark_turns = 0 AND ts.ani_turns >= 1 AND ccr.closed_at >= '2026-05-14'
)
AND validity = 'valid';

-- Verification:

SELECT 'quarantined' AS label, COUNT(*) AS n
FROM closed_conversation_records WHERE validity = 'invalid_fabrication';

SELECT 'still_valid' AS label, COUNT(*) AS n
FROM closed_conversation_records WHERE validity = 'valid';

SELECT 'remaining_fabrications_in_valid' AS label, COUNT(*) AS n
FROM closed_conversation_records ccr
JOIN (
    SELECT thread_id,
           SUM(CASE WHEN role = 'mark' THEN 1 ELSE 0 END) AS mark_turns,
           SUM(CASE WHEN role = 'ani'  THEN 1 ELSE 0 END) AS ani_turns
    FROM conversation_messages
    GROUP BY thread_id
) ts ON ts.thread_id = ccr.thread_id
WHERE ts.mark_turns = 0 AND ts.ani_turns >= 1
  AND ccr.closed_at >= '2026-05-14'
  AND ccr.validity = 'valid';
