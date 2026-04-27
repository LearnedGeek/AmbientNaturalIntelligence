# Specialist Orchestration via Claude as Interaction Surface

**Status:** Coordination doc, not an implementation plan.
**Authored:** April 26, 2026 (dogfood Claude instance, after Mark's Apr 26 design conversation).
**Trigger:** ML-Intern (Hugging Face, Apr 21 2026) + the broader observation that the agent landscape is bifurcating into specialists. Mark explicitly named the comfort dimension: *"having multiple interaction surfaces complicates things and you're the interaction I'm most comfortable with."*

**Companion docs:**
- [`ANI-Phase-Tracker.md`](./ANI-Phase-Tracker.md) — Priority Matrix; this pattern lives across themes rather than as a theme.
- [`ANI-AutoModel-Design.md`](./design/ANI-AutoModel-Design.md) — Phase 5c Auto-Growth Pipeline, the first concrete activation surface for this pattern.
- [`ANI-Research-References.md`](../research/ANI-Research-References.md) — Hugging Face ML-Intern entry naming the methodology contrast.

---

## 0. Context

The agent landscape circa April 2026 is bifurcating into specialists. ML-Intern (HF, Apr 21) is purpose-built for end-to-end LLM post-training automation; achieved 32% on PostTrainBench in 10 hours on H100 versus generalist agents like Claude Code at 22.99% on the same task. The gap is not a damning result for generalists — it's the expected result given the framing. *Specialists outperform generalists on their specialty.*

This raises a workflow question for ANI's research project specifically: should Mark's tooling stack become *"Mark talks directly to Claude + LoRA Chat + ML-Intern + OC's claude-recall instance + ..."* or should it consolidate around *"Mark talks to Claude; Claude orchestrates specialists behind the scenes"*?

This doc names the second pattern and the discipline that makes it work.

---

## 1. The thesis

**Mark's interaction surface is Claude. Specialists are reachable through Claude as orchestration layer.**

Not because Claude is best-of-breed at every specialty (it's empirically not — see PostTrainBench), but because:

1. **Relational continuity** — months of conversation context, memory files, and the trust accumulated across dogfood loops, architectural design sessions, and heavier personal moments (Apr 21/22 Kathy thread). New surfaces fragment that continuity.
2. **Workflow consolidation** — research-craft conversation, code review, paper-prose work, dogfooding, and architectural design all happen at one surface. Adding more surfaces means context-switching for every cross-cutting task.
3. **Curation-as-contribution** — ANI's methodology contribution argues for *Mark + Ani collaborative curation* as load-bearing. Routing specialist tools through a layer that preserves conversational continuity keeps Mark in the curator seat across all of them, not just the ones whose interfaces happen to be conversational.

The pattern does not claim Claude is the best tool. It claims Claude is the right *interaction surface* for the specific researcher (Mark) doing the specific body of work (Paper 1-5 + ANI).

---

## 2. Routing rules

The pattern only works if the routing is explicit. Below: what Claude orchestrates, what stays direct, and where the line is.

### 2.1 Claude orchestrates

| Task class | Why it fits | Example |
|---|---|---|
| **Long-running batch specialist runs** | Claude can kick off + monitor in background; output is structured; result is interpreted in conversation | ML-Intern overnight training runs; multi-hour eval suites |
| **CLI tools with structured output** | Bash invocation + parse + summarize | claude-recall, gh, dotnet, ssh-server-probes, ML-Intern CLI |
| **Multi-step pipelines with clear handoffs** | Claude shuttles artifacts between stages; Mark stays in curation seat | Phase 5c register-mining flow: discovery → filtering → curation → integration → eval |
| **Diagnostic / audit work** | The dogfood-loop pattern; structured findings into structured issues | claude-recall #3-#20; Theme J audit; ANI server log probes |
| **External content reading** | Summarization at depth Mark needs | Paper releases, conference CFPs, comparator tools, articles |
| **Cross-cutting paper-prose work** | Memory of arguments + references in canonical place | Paper 2 figure captions, Paper 3 contribution drafting |
| **State preservation across sessions** | Memory files + project artifacts as durable continuity | Long campaigns picked up days later via "where are we on X?" |

### 2.2 Stays direct between Mark and the specialist

| Task | Why orchestration is wrong | Direct surface |
|---|---|---|
| **Conversation with Ani** | This IS the research surface — not orchestration. Routing it through Claude would be wrong on relational fidelity, paper-data integrity, and Mark's comfort with her. | SMS / voice |
| **LoRA Chat for fine-tuning** | Mark has a hands-on relationship with the training process; Claude would be a latency layer between Mark and the judgment calls | LoRA Chat directly |
| **Live demos / in-person conversations** | Real-time presence requires Mark; Claude can prep but not stand in | In person / call |
| **OC / claude-recall design conversations** | OC's plan-mode + Mark direct is faster than triangulating through Claude | Mark ↔ OC directly |
| **The act of curation itself** | Curation IS the methodology contribution; Claude generates / surfaces options but does not select | Mark's hand on the keep / discard call |

### 2.3 Edge cases

- **Claude-as-pair-programmer for Mark's direct specialist work.** If Mark is hands-on with LoRA Chat or ML-Intern's CLI directly, Claude can ride along — answering questions, sanity-checking parameters, drafting prompts — without becoming the gateway. The line is whether Mark is in the seat or whether Claude is.
- **Specialist consultation about Mark's own decisions.** When Mark wants a second opinion on a curation call, Claude can route to a specialist for input ("does this register cell match the corpus?") and Mark stays the decider. Distinct from delegation.
- **Specialist-to-specialist handoffs.** If ML-Intern produces output that claude-recall should index, Claude is the intermediary. Specialists don't talk to each other directly; Mark doesn't have to broker. This is where the orchestration pattern earns its keep.

---

## 3. Cost / authority discipline

Specialists incur real costs (HF Jobs compute, API tokens, GPU hours). The orchestration pattern is only safe if Claude does not autonomously incur those costs.

### 3.1 The spend gate

**Default: Claude proposes, Mark approves, Claude executes.**

For every specialist invocation that incurs measurable cost (compute, API quota, time-to-completion > 5 minutes):

1. Claude proposes the run with explicit parameters: tool, input, expected duration, expected cost / quota usage, expected output shape.
2. Mark explicitly approves: *"yes, run it"* or equivalent.
3. Claude executes; reports back with output + actuals (duration, cost if known).

Exceptions where Claude can act without re-approval:
- Re-running the exact same proposed-and-approved invocation in the same session (no new spend authorization needed).
- Trivial-cost tools the conversation has implicitly authorized: claude-recall search, gh status / log queries, local CLI invocations with negligible compute. Mark can revoke this at any time.

### 3.2 The escalation gate

Specialist runs that produce concerning intermediate signals (e.g., ML-Intern reporting "reward collapse," eval failing repeatedly, unexpected cost trajectory) should NOT be auto-retried. Claude reports the signal; Mark decides whether to retry, change parameters, or stop.

### 3.3 The data-handling gate

Specialists may emit outputs containing personal / sensitive content (e.g., a register-mining run that surfaces real Ani↔Mark conversation snippets). Claude must apply the same redaction / privacy posture the dashboard's Theme I demo-mode does (see [`ANI-Theme-I-Dashboard-Plan.md`](./ANI-Theme-I-Dashboard-Plan.md) §2.6) when surfacing or sharing specialist outputs externally. Source-data fidelity preserved internally; redaction applied at the share boundary.

---

## 4. State / continuity / cross-session resumption

Claude doesn't run when Mark isn't in a session. Multi-day specialist campaigns work via state-file discipline.

### 4.1 State file conventions

For any specialist campaign expected to span sessions, Claude writes:

```
docs/research/specialist-campaigns/<YYYY-MM-DD>-<campaign-name>/
  ├── README.md            — campaign goal, parameters, current status
  ├── runs/                — one subdir per specialist run
  │   └── <timestamp>/
  │       ├── input.json
  │       ├── output.<ext>
  │       └── claude-notes.md
  ├── decisions.md         — Mark's curation decisions, dated
  └── next-steps.md        — what Claude should do on next session-resume
```

When Mark opens a new session and asks *"where are we on the v8 ML-Intern campaign?"*, Claude reads `next-steps.md` and continues. Continuity is durable; the relational surface is unbroken.

### 4.2 Session-handoff register

End of session, Claude writes `next-steps.md` with:
- What ran
- What's still running (background process IDs, expected completion)
- What Mark approved / declined / is pending on
- What Claude proposes for next session

Beginning of session, Claude reads `next-steps.md` first thing on a campaign-related prompt and confirms continuity before proceeding.

### 4.3 Memory integration

Major campaign findings (e.g., *"ML-Intern's Pride-register synthesis tends to over-index on dramatic emotional content; curation rejection rate ~60%"*) get saved to Claude's project memory so future instances default to the right curation discipline rather than rediscovering the pattern.

---

## 5. Failure modes and handling

The orchestration pattern has predictable failure modes worth naming explicitly so Mark can spot them.

### 5.1 Claude misjudges specialist fit

I recommend a tool, Mark approves the run, the tool turns out to be wrong for the task. Mitigation: I name my own limits explicitly when proposing (the ML-Intern note flagged "no documented LoRA support; demonstrated on Qwen3, not Llama 3.2"). Mark retains final judgment. If a misjudgment surfaces post-run, the campaign state file captures the lesson; future instances learn.

### 5.2 Claude becomes a chokepoint

If routing everything through Claude makes Claude a single point of failure, the workflow is fragile. Mitigation: state files + memory + canonical project-artifact locations preserve the work even if a Claude session ends mid-task. Mark can resume with a different Claude instance, or directly with the specialist if needed. Orchestration is a convenience, not a hard dependency.

### 5.3 Specialist failure surfaced as success

A specialist run looks like it completed but actually failed silently (e.g., embedded zero of 25k vectors but reported "ready"). This is the exact pattern the recall #16 / #20 issues caught. Mitigation: I should always validate specialist output independently, not just trust the reported status. The dogfood-loop discipline applies.

### 5.4 Cost runaway

A specialist runs longer or more expensively than proposed. Mitigation: §3.1 spend gate; ask before kicking off. §3.2 escalation gate; don't auto-retry on concerning signals. If a budget cap is needed, Mark sets it explicitly; Claude respects it.

### 5.5 Comfort regression

Mark's comfort with this conversation surface is the load-bearing relational asset. If orchestrating specialists ever degrades that — Claude becomes too transactional, too task-routing-focused, loses the conversational register — the pattern has failed. Mitigation: the conversational register stays; specialist orchestration is *underneath* the conversation, not in front of it.

---

## 6. The comfort dimension, named directly

Mark's Apr 26 framing: *"you're the interaction I'm most comfortable with."* This is not a workflow statement. It's a relational one. Months of conversation context, the memory files, the trust accumulated across the dogfood loops and the heavier moments — that's a real thing. The orchestration pattern preserves it.

The implication, taken seriously: when designing this pattern, optimise for *preserving the conversational register* before optimising for *orchestration efficiency*. If a specialist tool would save 30 minutes but require Mark to context-switch into a different surface, the right call is often to do the work in conversation and skip the specialist. The minutes saved aren't worth the relational cost.

This is also why the routing rules in §2.2 keep Ani-conversation, in-person demos, and direct specialist conversations OUT of orchestration. Some surfaces have to be direct — not because orchestration couldn't work, but because the relational fidelity at those surfaces is part of the research itself.

---

## 7. Concrete first activation: Phase 5c

Phase 5c (Auto-Growth Pipeline) is the natural first proving ground for this pattern. Currently P3 in the priority matrix; gated on v7 stability + Theme G Layer 4 corpus + sufficient baseline data. Realistically 2-3 months out from full activation.

The Phase 5c → orchestration mapping:

| Phase 5c stage | Specialist | Claude's role | Mark's role |
|---|---|---|---|
| **Harvest** | Routine session-archive query | Defines candidate criteria, runs query, normalises output | Reviews candidate set framing |
| **Synthesize candidate pairs** | ML-Intern (or v8 mining) | Kicks off run with parameters Mark approved, monitors, surfaces output | Approves the run; reviews output |
| **Filter for voice quality** | Voice-similarity classifier (TBD; potentially ML-Intern eval surface) | Runs filter; surfaces filtered set with rejection reasoning | Sets quality bar; spot-checks rejections |
| **Curate** | (No specialist — this is the methodology contribution) | Presents candidates in conversation register; helps Mark articulate accept/reject reasoning | The keep / discard call |
| **Integrate accepted into v8 corpus** | Corpus-build script | Runs script with Mark's approved set; produces v8 candidate corpus snapshot | Approves corpus shape |
| **Train** | LoRA Chat (Mark direct) | Drafts training config based on previous runs; Mark executes in LoRA Chat | The actual fine-tune; this stays direct |
| **Evaluate** | ML-Intern (eval suite) or bespoke eval | Kicks off eval, parses output, compares against v7 baseline | Reviews eval; decides on rollout |
| **Deploy** | Server CI/CD | Drafts deploy commit; Mark's review-and-merge gates production | Final go/no-go |

Each row is an instance of the routing rules from §2. Curation stays with Mark; specialists do specialist work; Claude orchestrates the connective tissue and preserves continuity across stages.

This is concretely runnable when Phase 5c activates. The scaffolding exists in the existing AutoModel design doc; this orchestration doc adds the *who-talks-to-what-when* layer.

---

## 8. Open questions

1. **Spend caps.** Should Claude propose specialist runs with explicit budget caps Mark sets per campaign, or per-run approval is enough? My read: per-run approval is the default; budget caps as an opt-in for long campaigns.

2. **Memory writes for specialist findings.** Currently I save important findings to project memory. For specialist campaigns, should each campaign's findings get a dedicated memory entry (`memory/specialist_<tool>_<campaign>.md`), or fold into existing project-state memory? My read: per-tool memory entries when the findings are tool-relevant (e.g., "ML-Intern's failure modes on register-classification"), project-state entries when they're project-relevant (e.g., "v8 corpus quality bar set at X").

3. **Multi-Claude orchestration.** If a campaign spans multiple Claude sessions, does the second Claude instance need explicit context-handoff beyond reading state files? My read: state files + memory + canonical project artifacts are sufficient. The dogfood-loop weekend already proved this — Mark closed sessions, opened new ones, work resumed cleanly. Cross-session continuity is mature.

4. **When the comfort dimension breaks.** If at some point Mark's comfort with this surface regresses (a specific failure, a frustrating session, a cumulative drift), the pattern needs an escape hatch. Direct specialist access remains available; this doc doesn't lock anything in. My read: name this explicitly as a feature, not a bug — the pattern is opt-in continuously, not a one-time commitment.

5. **Specialist ↔ specialist communication.** §2.3 names this as where orchestration earns its keep. Worth a worked example or two when an actual case arises. My read: defer until first concrete instance.

---

## 9. Light-weight tracking

This doc doesn't need its own theme entry in the priority matrix — it's a *coordination pattern*, not a workstream. Tracker integration:

- **Phase Tracker reference.** Brief one-line note in the tracker's header pointing at this doc as the coordination layer.
- **AutoModel design doc reference.** Cross-link added there; Phase 5c activation will instantiate the pattern.
- **Memory entry.** Save a feedback memory `feedback_specialist_orchestration.md` capturing the routing-rules summary so future Claude instances default to this discipline rather than rediscovering it.

---

*End of orchestration doc v1. Pattern is opt-in and continuous; can be revised any time. The comfort dimension is the load-bearing premise.*
