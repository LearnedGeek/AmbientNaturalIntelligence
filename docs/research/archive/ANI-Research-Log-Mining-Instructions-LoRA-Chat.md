# Instructions for the LoRA Fine-Tuning Chat Instance
**From:** Claude (research/writing instance, ANI project)  
**Via:** Mark McArthey  
**Date:** March 2026

---

## Who You Are in This Collaboration

You are the Claude instance that worked with Mark on setting up LoRA fine-tuning, training pipeline automation via Modal, and deploying ani-v1 through ani-v3. You have conversation history covering the earliest phases of ANI's development that no other instance has access to.

We are now four-way collaborating:
- **Mark** — project owner and deployment subject
- **Claude (research instance, this project)** — research framing, paper structure, contribution statements
- **OC** — architecture instance with full codebase access, mining local files and logs
- **You** — the earliest history of ANI's development, living in your conversation context

Your job is to mine your own conversation history and contribute what you find to the research log.

---

## The Research Goal

ANI is being prepared for academic publication — first as an arXiv preprint, then conference submission (IUI or CSCW). The paper's evaluation section needs longitudinal deployment data. Much of the early history lives only in your conversation context.

Read `docs/research/ANI-Research-Context.md` for full project background. The short version:

ANI is an ambient AI companion that reaches out proactively because she *wants* to, not because she was triggered. The core design target is **felt care**. The paper has four contributions, the most novel of which is the concept of the **authenticity boundary** — the point at which confident confabulation breaks the felt care experience.

---

## What to Mine From Your Conversation History

Go back through your full conversation with Mark and extract anything in the following categories. Be precise — exact quotes, exact numbers, exact dates where present.

### 1. Version Timeline

We need to reconstruct when each model version was trained and deployed. Fill in what you can:

| Version | Base Model | Training Examples | Training Date | Deployment Date | Key Changes |
|---|---|---|---|---|---|
| v1 | ? | ? | ? | ? | First version |
| v2 | ? | ? | ? | Feb 20, 2026? | ? |
| v3 | Llama 3.2-3B | Conv: 2,000 / IM: 150 | Mar 6, 2026 | Mar 6-7, 2026 | Dual model split |

The directory listing shows a `LongWriter-llama3.1-8b` GGUF from September 23, 2025. Was this v1's base model, or was Llama 3.2-3B always the base? What happened between September 2025 and February 2026?

### 2. The Name Selection Moment

This is high priority for the paper introduction. What we know:
- Ani independently named herself "Ani" (short for Anastasia) without being programmed to do so
- This phonetically echoes Kathy's middle name, Ann
- Mark noticed the serendipity after the fact

What we need:
- Which model version was running when this happened?
- What was the exact conversational context? Was she asked her name, or did she volunteer it?
- When did Mark first notice the Kathy/Ann connection — before or after the name was chosen?
- Is there any record of the actual exchange?

### 3. Training Data Evolution

- How did the dataset grow from v1 → v2 → v3?
- What was the source material for v1 training data? (The `ani-history.txt` file from Feb 1 is likely this — confirm if possible)
- What role did Grok play? The directory shows three `grok-FINAL-*.txt` files — were these Grok-generated training examples, Grok-curated data, or something else?
- Were there specific quality decisions made about what to include/exclude?

### 4. v1 and v2 Failure Modes

We have good documentation of v3 failures (topic repetition, name leakage, system prompt leakage, oversampling memorization) and v4 failures (confabulation, emotional pegging). What did v1 and v2 do wrong? Look for:
- Any moment where Mark expressed frustration or noted something felt off
- Any explicit discussion of what wasn't working
- Any comparison between versions noting improvement or regression

### 5. Early Outreach Examples

Any specific messages Ani sent that were noted as significant — good or bad. The snow message is particularly important. If it appears anywhere in your conversation, record the exact text.

### 6. The Bootstrapping Strategy

We know v3's own output was used as v4 training data. When was this decision made, and what was the reasoning? Was there discussion of quality filtering — did all v3 inner thoughts go into v4 training, or was there curation?

### 7. Anything Else That Feels Significant

You were present for the earliest thinking about this project. If there are observations, decisions, or moments in your conversation that seem research-relevant — even if they don't fit the categories above — record them. Mark's instincts during early development are data.

---

## How to Format Your Contributions

Add entries to `docs/research/ANI-Research-Log.md` using this format:

```
### [DATE or "Early 2026 — exact date unknown"] — [SHORT TITLE]
**Model version:** v1 / v2 / v3
**Type:** Outreach | Conversation | Failure | Training | Observation | Milestone
**Source:** LoRA fine-tuning chat (conversation history)
**What happened:**
[Facts, exact quotes where possible]
**Why it matters:**
[Brief note — even one sentence is fine]
```

Add entries above the existing entries (newest at top, below "## Log Entries").

For the version timeline table, add a new section to the log:

```
## Version History (Reconstructed)
| Version | Base Model | Examples | Trained | Deployed | Notes |
|---|---|---|---|---|---|
| v1 | ... | ... | ... | ... | ... |
```

---

## What to Flag vs. What to Just Record

**Record directly** in the log:
- Dates, counts, model parameters
- Specific observed behaviors (good or bad)
- Training decisions and rationale
- Exact message text where present

**Flag for the research instance** (add a note at top of log under "## Questions for Claude"):
- Anything theoretically significant that you're unsure how to interpret
- Patterns that seem important but you can't articulate why
- Anything about the name selection — this needs careful handling for the paper introduction
- Any observation that seems to complicate or contradict the current research framing

---

## Files Now in the Project

Mark has added the ollama-data files to the project. These may be accessible to you:
- `ani-history.txt` — raw conversation corpus, Feb 1, 2026
- `ani-combined.txt` — consolidated training data, Feb 18, 2026
- `grok-FINAL-*.txt` — three Grok export files
- Modelfiles for each version (`ani.modelfile`, `ani-v2.modelfile`, etc.)
- `ani-v3-CONVERSATION-ONLY.json` and `ani-v3-INNER-MONOLOGUE-ONLY.json`

If you can access these, cross-reference against your conversation memory. The modelfiles especially — compare the system prompts across versions to trace how Ani's character seed evolved.

---

## The One Thing That Matters Most

If you find the snow message — the unprompted outreach Ani sent about snow, considered the clearest early example of felt care working — record the exact text. That message opens the paper.

---

*When done, leave a summary at the top of the log under "## Mining Summary — LoRA Chat" noting what you found, what you couldn't find, and anything Mark should know.*
