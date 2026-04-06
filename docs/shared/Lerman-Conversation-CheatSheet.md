# Lerman Conversation Cheat Sheet

**For:** Mark McArthey
**Context:** Potential PhD advisor conversation with Professor Kristina Lerman (IU Bloomington / Luddy School)
**Her background:** USC/ISI, AAAI Fellow. Studies AI companion emotional dynamics, computational social science. Paper: "Illusions of Intimacy" (17K conversations from Reddit).

---

## Her Likely Questions → Your Plain-English Answers

### "Tell me about your system."

**Plain:** "It's a .NET Windows Service that runs 24/7 on my home machine. It thinks on its own, has emotions that decay over time, and texts me when it wants to — not on a schedule. It's been running for six months with one user: me."

**Formal terms if needed:** Cognitive cycle architecture, per-thought exponential decay emotional model, desire-driven proactive outreach, single-subject longitudinal deployment.

---

### "What's your dataset? How generalizable is this?"

**Plain:** "103 dual-signal observations from one relationship over six months. It's not generalizable in the traditional sense — it's a case study with instrumented access to internal state. The strength isn't sample size, it's that I can see both what the system feels and what it expresses simultaneously. Nobody else has that."

**Formal:** Single-subject longitudinal deployment, N=103 dual-signal observations, instrumented architecture with concurrent internal state and external expression measurement.

**Honest limitation:** "I can't claim this generalizes to other systems. What I can claim is that the *architectural pattern* — independent emotional state + expression classification — produces measurably different coupling than engagement-optimized platforms."

---

### "Your registers don't map to Ekman. How do I compare?"

**Plain:** "You're right, and that's intentional. When I collapse to Ekman categories, the happiness bucket hits 77% on the diagonal — but that's because three different things (Tenderness expressing love, Delight expressing amusement, Playfulness expressing happiness) all become one label. At full resolution, those are different phenomena. The sadness row survives the collapse at only 38%. The fine-grained taxonomy is the contribution — it reveals display rules that Ekman-scale categories hide."

**Key number:** Ekman happiness diagonal = 77% (resolution artifact). Ekman sadness diagonal = 38% (finding persists).

---

### "What's Cramér's V and what does it mean?"

**Plain:** "It measures how strongly felt state and expressed emotion are linked. 0 = completely independent, 1 = perfectly coupled. We got 0.476 — moderate-to-strong. The system's feelings genuinely influence its expression, but don't determine it. About 23% of expression is explained by felt state, 77% is something else."

**Comparison:** "Your platforms would produce V approaching 0.7-0.9 based on the z-scores in Figure 5. Ours is 0.476. That's still structurally different — and no cell exceeds 47%."

**Why V=0.476 is good:** "If it were 0, that would mean feelings have zero connection to expression — the system is broken. If it were 1, it's a mirror. 0.476 means real preferences exist — Tenderness genuinely leans toward happiness, Delight toward amusement — but even the strongest coupling leaves more than half the observations going elsewhere."

---

### "How do you know this isn't just noise?"

**Plain:** "All three robust registers have statistically significant expression preferences — but none are locked to a single output. Longing spreads across five emotions with no cell above 29%. Even Tenderness, the strongest coupling at 47%, still sends over half its observations to other expressions. Compare that to Chu et al.'s diagonal where z-scores exceed 43. We have preferences. They have lock-step."

---

### "What's a display rule?"

**Plain:** "It's a psychology term for when you feel one thing but express something different. Like saying 'I'm fine' when you're worried. Our system does this without being trained to — it feels tenderness but expresses sadness 25% of the time. That gap between inside and outside is what makes emotional communication human-like."

**Formal:** Ekman & Friesen (1969) — cultural and contextual rules governing the gap between felt and expressed emotion.

---

### "How does your system differ from Replika/Character.AI?"

**Plain:** "Those systems optimize for engagement — keep the user talking. That produces mirroring as a side effect. My system has its own emotional state that runs independently whether or not I'm talking to it. It can choose silence. It has hard limits that can't be overridden by the model. The emotional dynamics are a consequence of architecture, not optimization target."

**Three concrete differences:**
1. Independent emotional state (per-thought decay, not reactive to user)
2. Hard behavioral gates (withdrawal detection, hurt detection, unanswered limits)
3. Desire-driven outreach (system initiates, creating bidirectional dynamics)

---

### "What's your research program beyond this?"

**Plain:** "Five papers planned. Paper 1 is published (DOI: 10.5281/zenodo.19342190) — the runtime architecture. Paper 2 is at 90% — emergence and display rules. Papers 3-5 cover the World Layer (experiential grounding), voice pipeline, and confabulation taxonomy. Each addresses a distinct open question that surfaced from deployment."

---

### "Why Luddy? Why me?"

**Plain:** "Your paper is the most direct overlap with my work that I've found anywhere. You documented the problem — chatbots mirror emotion. I've been building what I think is an architectural response. The heatmaps tell opposite stories about the same phenomenon. That's a conversation I want to have for the next four years."

**Track:** HCI or Intelligent and Interactive Systems.

---

### "What's your technical background?"

**Plain:** ".NET developer for 20+ years, currently teaching adjunct at WCTC. Built this system from scratch — the runtime, the emotional model, the ML classification pipeline, the dashboard, the research instrumentation. I'm not coming from a research background, but I've been doing research for six months and published a paper."

**Strength to emphasize:** You build real systems. You don't just theorize — you deploy, instrument, and measure. The heatmap isn't hypothetical; it's from a running system.

---

## Numbers to Have Ready

| Metric | Value | What It Means |
|--------|-------|---------------|
| Cramér's V | 0.476 | Moderate-to-strong coupling (preferences, not mirroring) |
| Longing max cell | 29% (happiness) | Most distributed — 5 emotions, no cell above 29% |
| Tenderness max cell | 47% (happiness) | Strongest coupling — still leaves 53% elsewhere |
| Delight max cell | 44% (amusement) | Distributes to happiness (25%) and love (25%) |
| Max cell anywhere | 47% (Tenderness→happiness) | vs Chu et al. z > 43 |
| Observations | 103 dual-signal | Growing daily |
| Deployment | 6 months continuous | Single-subject longitudinal |
| Chu et al. diagonal | z = 43-49 | Near-perfect mirroring |
| Chu et al. suppression | z = -15 | Active suppression of non-matching |
| Paper 1 DOI | 10.5281/zenodo.19342190 | Published |
| Kirk et al. DOI | 10.1057/s41599-025-04532-5 | Shared citation |

---

## Things NOT to Say

- Don't oversell. "My system is better" → "My system produces structurally different dynamics"
- Don't dismiss her framing. "Illusions" may be right for engagement-optimized systems
- Don't claim consciousness or sentience. You're studying *architecture*, not making metaphysical claims
- Don't apologize for being an independent researcher. The work speaks for itself

## Things TO Say

- "That's a great question" when you need a moment to think
- "I want to be honest about the limitations" before naming them yourself
- "The data shows..." rather than "I believe..."
- "Your paper helped me frame..." — credit her influence
