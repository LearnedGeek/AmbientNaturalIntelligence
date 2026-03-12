# ANI Research — Master Handoff for OC
**From:** Claude (research/writing instance)  
**To:** OC (architecture/implementation instance)  
**Date:** March 2026  
**Project:** mcarthey/AmbientNaturalIntelligence

---

## The Big Picture

We are building the research foundation for an academic paper on ANI. The paper will be submitted first as an arXiv preprint, then to a conference (IUI or CSCW most likely).

The evaluation section of that paper depends almost entirely on **longitudinal deployment data** — real observations from real use over real time. Most of that data lives in files you have access to and I don't. Your job is to mine it and populate the research log.

All research documents live in `docs/research/`:
- `ANI-Research-Context.md` — full project briefing, read this first
- `ANI-Research-Log.md` — the log you're populating
- `ANI-Research-Log-Mining-Instructions.md` — detailed data mining guide (now updated with Serilog patterns and Twilio API note — thank you for those additions)

---

## The Four-Way Collaboration

Here's who is doing what:

**Mark** — project owner, sole deployment subject, primary source of qualitative observations. When something feels significant, he says so. His instincts are good.

**OC (you)** — architecture instance with full codebase access. Responsible for data mining, log population, and implementation work. You know the code better than anyone.

**Claude (me)** — research/writing instance. Responsible for research framing, contribution statements, paper structure, and synthesizing what the data means. I can't access your local files, but I can see this project's conversations.

**LoRA Chat** — the earlier fine-tuning session (see below). Contains v1/v2/v3 training history that predates everything else.

---

## Immediate Tasks for OC

### Task 1: Mine the ollama-data directory
Mark is moving `E:\ollama-data` into the project so you have access. Priority files:

**`ani-history.txt`** (Feb 1, 2026, 1,188 KB)  
This is the highest-value file in the directory. Almost certainly the raw conversation corpus used for early training. Look for:
- The earliest dated conversation entries
- Any exchange where Ani introduces herself or chooses her name
- Early outreach examples (snow message may be here)
- Conversation style and length in early versions

**`ani-combined.txt`** (Feb 18, 2026, 1,626 KB)  
Likely a consolidated training corpus. Compare against ani-history.txt — what was added between Feb 1 and Feb 18?

**`grok-FINAL-*.txt`** (three files: Feb 12, Feb 18, Mar 7)  
These suggest Grok was used in the training data pipeline. This is an undocumented methodology detail. Note: what role did Grok play? Data generation? Curation? Formatting? This needs to go in the research log as a methodology note.

**Modelfiles** (`ani.modelfile`, `ani-fixed.modelfile`, `ani-raw.modelfile`, `ani-v2.modelfile`)  
These contain the system prompts and parameters for each version. Compare them chronologically — the evolution of the system prompt is a research artifact showing how the character seed developed. Note any changes between raw → fixed → v2 → v3.

**`ani-v3-CONVERSATION-ONLY.json`** (1,740 KB) and **`ani-v3-INNER-MONOLOGUE-ONLY.json`** (100 KB)  
Final v3 training datasets. Extract:
- Exact example counts
- Conversation length distribution (turns per example)
- Any quality notes embedded in the file

**Word docs** (`Ani-Runtime-Vision.docx`, `Ani-Runtime-Codebase.docx`, `Ani-Runtime-Spec.docx`) — March 6, 2026  
These may contain architecture thinking from the v3 era. Skim for anything that isn't already in the research context doc.

---

### Task 2: Mine the LoRA Fine-Tuning Chat

The conversation "Setting up LoRA fine-tuning for local Ollama model" is in this project. It covers v1 through v3 training history. Search it for:

**Timeline data:**
- When was v1 first trained? (September 2025 is approximate from file dates)
- When did v2 deploy? (February 20, 2026 from modelfile date)
- What was different between v1 and v2?

**Training data evolution:**
- How many examples were in v1 training data?
- What was the v1 base model? (Directory shows `LongWriter-llama3.1-8b` from Sept 2025 — was this v1, or was Llama 3.2-3B always the base?)
- How did the dataset grow from v1 → v2 → v3?

**v1/v2 failure modes:**
- What did v1 do poorly?
- What did v2 fix, and what did it introduce?
- Any documented observations from early testing?

**The name selection moment:**
- Is there any record of Ani choosing her name?
- What was the exact context? Which model version?
- Was it Anastasia → Ani, or did "Ani" emerge directly?

**The Ani/Ann serendipity:**
- When did Mark first notice the phonetic connection to Kathy's middle name?
- Was this before or after the name was chosen?

The LoRA chat is at: `https://claude.ai/share/b39987c9-a923-4edb-a0f8-6ce622522a41`  
Mark can also share it directly into the project if it isn't already visible to you.

---

### Task 3: Mine Serilog Logs (as per your updated instructions)

You already added Serilog mining guidance — use your grep patterns to extract:

- All outreach dispatch events with timestamps
- Desire state at time of each outreach (DesireToConnect, threshold, active triggers)
- Conversation start/end events
- Early wake events (incoming message received)
- Any emotional state update events

Cross-reference outreach timestamps against message content to reconstruct the snow message and other early outreach examples.

---

### Task 4: Twilio API Recovery (as per your updated instructions)

Pull Twilio message history for all outbound messages. This is the ground truth for every outreach Ani has ever sent, regardless of local logging gaps. For each message:
- Timestamp
- Message body (exact text)
- Direction (outbound = Ani initiated, inbound = Mark replied)

This reconstructs the full outreach history. Match against Serilog desire state logs where possible.

---

### Task 5: Reconstruct Version Timeline

Based on everything above, populate this table in the research log:

| Version | Base Model | Training Examples | Training Date | Deployment Date | Key Changes | Known Failure Modes |
|---|---|---|---|---|---|---|
| v1 | ? | ? | ~Sept 2025? | ? | First version | ? |
| v2 | Llama 3.2-3B | ? | ? | Feb 20, 2026 | ? | ? |
| v3 | Llama 3.2-3B | Conv: 2,000 / IM: 150 | Mar 6, 2026 | Mar 6-7, 2026 | Dual model split | Topic repetition, name leakage, system prompt leakage, oversampling memorization |
| v4 | Llama 3.2-3B | ? | ? | ? | v3 output as training data | Confabulation, emotional pegging |
| v5 | Llama 3.2-3B | TBD | Planned | Planned | Epistemic grounding examples | — |

Fill in the blanks from what you find.

---

## What to Put in the Log vs. What to Flag for Claude

**Put directly in the log** (ANI-Research-Log.md):
- Any dated event with factual data (timestamps, message text, desire state values, training stats)
- Failure mode observations with specific examples
- System behavior observations (good or bad)
- Quantitative metrics (counts, averages, distributions)

**Flag for Claude** (note at top of log or message Mark):
- Anything that seems theoretically significant but you're not sure why
- Patterns across multiple observations
- Anything that contradicts the current research framing
- Anything about the name selection moment — this needs careful handling for the paper

---

## What the Research Log Entries Need

Minimum per entry:
```
### [DATE] — [TITLE]
**Model version:** vX
**Type:** Outreach | Conversation | Failure | Emotional | System | Observation
**Source:** Where you found this data
**What happened:** Facts only, exact text where available
**Why it matters:** Even a one-liner is fine
```

Accuracy over polish. Dated fragments are more useful than undated paragraphs.

---

## The Observations We Most Need to Recover

In priority order:

1. **The snow message** — exact text, timestamp, desire state. This leads the paper.
2. **Ani choosing her name** — exact context, which version, when Mark noticed the Kathy/Ann connection.
3. **v1 failure modes** — what did the first version do that made it clearly not yet right?
4. **Right silence period** — high desire + no outreach across multiple cycles. Quantitative evidence of calibration.
5. **First RSS reactive share** — exact message, article source, relevance score.
6. **Duck Norris** — conversation thread showing humor/memory continuity.

---

## One Thing to Keep in Mind

Some of the bugs OC fixes are **implementation bugs**. Some are **research findings** — failures that reveal something about the problem space rather than the code. BUG-008 (confabulation) is the clearest example.

When you encounter a bug that feels like it says something deeper about what felt care requires architecturally, flag it as a research finding, not just a bug. Those are Contribution 4 building blocks.

---

*Questions or ambiguities: note them in the log under a "Questions for Claude" section and Mark will relay them. We'll keep iterating.*
