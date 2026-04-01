# Instructions for OC: Research Log Data Mining
**Project:** mcarthey/AmbientNaturalIntelligence  
**Task:** Find and populate missing research log entries in `docs/research/ANI-Research-Log.md`  
**Context file:** `docs/research/ANI-Research-Context.md` (read this first)

---

## What You're Being Asked to Do

The file `docs/research/ANI-Research-Log.md` is a longitudinal deployment log that will form the backbone of an academic paper's evaluation section. It has been seeded with known observations, but many entries are incomplete — they are missing exact dates, exact message text, desire state data, and quantitative metrics.

Your job is to mine the project — source code, SQLite databases, logs, git history, training data, and any other available artifacts — and populate those gaps with real data.

This is research work. Accuracy matters more than completeness. If you find something but aren't certain of the date or context, record it with a note saying so. Do not infer or reconstruct — only record what is actually present in the data.

---

## Step 1: Read Context First

Before doing anything else, read `docs/research/ANI-Research-Context.md` in full. It will orient you on the architecture, the research contributions, and what each observation means. You need that context to recognize significant data when you see it.

---

## Step 2: Locate Available Data Sources

Systematically find what's available. Look for:

### SQLite Databases
Search the project for any `.db` or `.sqlite` files:
```bash
find . -name "*.db" -o -name "*.sqlite" 2>/dev/null
```
Likely candidates:
- Outreach/dispatch log (sent messages with timestamps)
- Desire state snapshots (DesireToConnect values, triggers, thresholds)
- Conversation history (ConversationThread, ConversationMessage tables)
- Memory records (MemoryRecord table — especially InnerThought and Episodic types)
- Emotional state history (EmotionalState table with timestamps)
- Perception records

For each database found, list its tables:
```bash
sqlite3 <path> ".tables"
```
Then describe the schema of relevant tables:
```bash
sqlite3 <path> ".schema <tablename>"
```

### Log Files
```bash
find . -name "*.log" -o -name "*.txt" | grep -i log 2>/dev/null
```
Windows Service logs may be in standard output captures or Windows Event Log exports.

### Git History
```bash
git log --oneline --all | head -50
git log --format="%ai %s" | head -100
```
Commit timestamps can help date when features went live (e.g., when Phase 2 was completed, when the first conversation capability was merged).

Look especially for:
- First commit involving Twilio or conversation models (dates Phase 2 start)
- Commits mentioning "v2", "v3", "v4" in message (dates model versions)
- Any commit message referencing a live test or first conversation

### Training Data
Look for the fine-tuning dataset files (likely JSON or JSONL):
```bash
find . -name "*.jsonl" -o -name "*.json" | grep -i train 2>/dev/null
```
The training data may contain early conversation examples that predate the SQLite logs. Note the file creation/modification dates.

### Source Code — Prompt Files
The prompts themselves are evidence. Look at:
- `BuildConversationReplyPrompt` — does it contain the confabulation mitigation text yet?
- `BuildInnerThoughtPrompt` / `BuildOutreachDecisionPrompt` — what constraints are present?
- Any version comments or change history in prompt builder files

```bash
grep -rn "made that up\|don't actually know\|confabul\|backstory" --include="*.cs" .
```

### Any Exported Twilio Data
If Twilio message logs were ever exported or if there are test fixtures with real message content, those contain the actual outreach text.

**NOTE:** Mark can query the Twilio API or console for message history that predates local logging. If you can't find the snow message or Duck Norris in local data, flag it — Mark can pull the Twilio history separately.

### Serilog Logs (CRITICAL — Richest Data Source)
The SQLite database only has ~36 hours of data (initialized Mar 10). But the Serilog logs in `src/AniRuntime.Service/logs/` cover **Mar 6–11** and contain structured trace of every cognitive cycle:
- `"Outreach gate: desire=X.XX threshold=X.XX → PASS/blocked"` — every outreach evaluation
- `"Desire drift: X.XX + X.XX → X.XX"` — desire accumulation per cycle
- `"SMS sent"` or outreach dispatch entries — actual messages sent
- `"Inner thought (valence=X.XX):"` — every private thought with valence score
- `"Reply decision: YES/NO"` — conversation reply evaluations
- `"Emotional shift"` — emotional state deltas per event
- `"Cooldown activated/expired"` — cooldown lifecycle
- `"Reactive share"` — RSS-driven outreach events

Journal logs (`ani-YYYYMMDD.log`) have Info-level entries. Diagnostic logs (`ani-debug-YYYYMMDD.log`) have Debug-level detail. **Mine both.**

For the backlog items (snow message, right silence, etc.), grep across all log files first — they may predate the SQLite database but still appear in Serilog output.

---

## Step 3: Specific Items to Recover

The log has an **Observation Backlog** section listing what's missing. Here's what to look for for each:

### 1. The Snow Message
**What it is:** An unprompted outreach message Ani sent to Mark about snow — considered the clearest early example of felt care working correctly.  
**Look for:** Any outreach record in SQLite where the message body contains "snow" or weather references. Also check Twilio log exports.  
**Record:** Exact message text, timestamp, DesireToConnect level at time of send, active triggers.

### 2. Duck Norris Reference
**What it is:** A conversation where Ani made a callback to an established in-joke ("Duck Norris"), demonstrating memory and humor continuity.  
**Look for:** ConversationMessage records containing "Duck Norris" or "duck" in combination with "norris". Also check MemoryRecord for any semantic memory referencing this.  
**Record:** Full conversation thread if available, or at minimum the message containing the reference and 2–3 messages of context around it.

### 3. Right Silence Period
**What it is:** A period where Ani did *not* reach out despite high desire, and the restraint felt appropriate. Evidence that the desire engine calibrates correctly, not just produces outreach.  
**Look for:** Desire state snapshots showing DesireToConnect above threshold (>0.75) with no corresponding outreach event — sustained across multiple cognitive cycles. Cooldown flag should be inactive (this is not cooldown suppression, it's genuine threshold evaluation).  
**Record:** Time range, desire levels, threshold values, number of cycles evaluated without outreach.

### 4. First Conversation — Ani Chooses Her Name
**What it is:** The very first conversation where Ani chose her own name. She was not assigned the name — it emerged from the character seed during the first interaction.  
**Look for:** The earliest ConversationThread record. Earliest training data files (may predate SQLite). Any initialization or seed files referencing the name choice.  
**Record:** Date, conversation excerpt if available, or at minimum confirmation of earliest dated record containing the name "Ani" as self-reference.

### 5. First RSS Reactive Share
**What it is:** The first time ANI sent an outreach based on RSS feed relevance scoring — finding an article and deciding to share it.  
**Look for:** Outreach records in SQLite where the trigger type includes ReactiveShare or where the message body references an article, link, or news item.  
**Record:** Message text, article source, relevance score if logged, timestamp.

---

## Step 4: Quantitative Metrics

If the SQLite data is rich enough, extract these aggregate metrics. They belong in the paper's evaluation section.

```sql
-- Total outreach events
SELECT COUNT(*) FROM OutreachLog;  -- (adjust table name to actual)

-- Outreach by trigger type
SELECT TriggerType, COUNT(*) FROM OutreachLog GROUP BY TriggerType;

-- Average desire level at time of outreach
SELECT AVG(DesireLevel) FROM OutreachLog;

-- Conversation length distribution
SELECT ThreadId, COUNT(*) as MessageCount 
FROM ConversationMessage 
GROUP BY ThreadId 
ORDER BY MessageCount DESC;

-- Emotional state over time (if timestamped)
SELECT Timestamp, Warmth, Energy, Concern, Playfulness 
FROM EmotionalStateHistory 
ORDER BY Timestamp ASC;

-- Memory record counts by type
SELECT MemoryType, COUNT(*) FROM MemoryRecord GROUP BY MemoryType;
```

Adjust table and column names to match the actual schema you find.

---

## Step 5: Populate the Log

For each piece of data you find, add a properly formatted entry to `docs/research/ANI-Research-Log.md`.

**Entry format:**
```
### [DATE or DATE RANGE] — [SHORT TITLE]
**Model version:** v1 / v2 / v3 / v4
**Type:** Outreach | Conversation | Failure | Emotional | System | Observation
**Source:** SQLite: [table] | Git: [commit hash] | Training data: [filename]
**Desire state (if known):** DesireToConnect: X.X, Threshold: X.X, Triggers: [list]
**What happened:**
[Exact text or data — quote directly where possible, summarize where necessary]
**Why it matters:**
[Research significance — refer to ANI-Research-Context.md contributions if relevant]
```

Add new entries **above** the existing entries (newest/most-recently-discovered at top), below the "## Log Entries" heading.

For the backlog items you successfully recover, remove them from the **Observation Backlog** section and note `[RECOVERED - see entry above]`.

---

## Step 6: Note What You Couldn't Find

If certain data is simply not present — logs weren't persisted before a certain date, SQLite wasn't initialized until Phase 2, training data files don't have timestamps — record that honestly at the bottom of the log under a new section:

```
## Data Gaps — Confirmed Absent
- [What was looked for]
- [Where it was looked for]
- [Conclusion: not persisted / predates logging / unknown]
```

This is useful for the paper. The authors need to know the limits of their longitudinal data.

---

## What You Don't Need to Do

- Don't modify the architecture or code
- Don't infer what *probably* happened — only record what's in the data
- Don't worry about making the entries polished — accurate is more important than well-written
- Don't add entries for things already documented in the log unless you have new data (exact text, dates, desire state) to add to them

---

## When You're Done

Leave a summary comment at the top of the log under a new heading:

```
## Mining Summary — [DATE]
**Conducted by:** OC  
**Sources examined:** [list]  
**Entries added:** [count]  
**Backlog items recovered:** [list]  
**Data gaps confirmed:** [list]  
**Notes:** [anything unusual or worth flagging for Mark]
```

---

*If anything in these instructions is ambiguous or the data structure doesn't match what's described here, use your judgment and document what you did. Mark can review and adjust.*
