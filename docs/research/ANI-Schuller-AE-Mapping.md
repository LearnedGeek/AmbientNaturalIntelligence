# ANI × Schuller AE Framework — Gap Analysis & Roadmap

**Source:** Li, Sun, Schlicher, Lim & Schuller (2025) "Artificial Emotion: A Survey of Theories and Debates on Realising Emotion in AI" (arXiv:2508.10286v2)
**Date:** April 7, 2026
**Purpose:** Map ANI's architecture against Schuller's Table I (AE functions by architectural level) to identify what we have, what's close, and what's missing.

---

## Table I Mapping: ANI vs Schuller Framework

### Communicative Aspects

| AE Function | Schuller Status | ANI Status | Details | Gap / Opportunity |
|---|---|---|---|---|
| **Label-conditioned affective generation** | Mature | **Partial** | ElevenLabs v3 with 1,806 audio tags catalogued. Voice tag selection uses emotional state + ML classification. | **Current delivery is flat.** Tag selection needs semantic matching (Phase 2 of voice tag pipeline). The tags exist as an "alphabet" — the system writes them but delivery quality is inconsistent. "Bad acting" in outreach audio needs work. Priority: improve tag-to-emotion mapping for natural prosody. |
| **Social affect expression** | Partial | **Yes — deployed** | Desire-driven outreach, hurt detection, withdrawal, care detection, silence as active choice. 6 months continuous deployment. | Schuller frames this as equally about engagement AND well-being. ANI deliberately does NOT optimize for engagement — the desire engine optimizes for authenticity. This is a philosophical distinction worth noting: ANI's social affect expression is designed to resist the engagement trap that Chu et al. (2025) documented. |
| **Introspective affect reporting** | **Absent** | **Substrate exists** | Heuristic emotional state (internal) + ML expression (external) run independently. Divergence measured (Cramér's V = 0.476). State-expression gap is empirically observable. | **Narration layer planned for Paper 3.** One architectural addition: give the inner thought model access to its own divergence score and let it narrate the gap. "I feel tender right now but my words are coming out sad." Would be the first deployed instance of what Schuller rates as Absent. |

### Architectural Aspects — Display Models

| AE Function | Schuller Status | ANI Status | Details | Gap / Opportunity |
|---|---|---|---|---|
| **Standardised evaluation metrics** | **Absent** | **Partial** | Cramér's V for state-expression coupling. ResonanceScore for longitudinal emergence. Per-register uniformity tests. | Not standardized across systems — these are ANI-specific metrics. Could propose Cramér's V on state-expression coupling as a standard metric for AE systems. Paper contribution potential. |

### Architectural Aspects — Process Models (Low-Level)

| AE Function | Schuller Status | ANI Status | Details | Gap / Opportunity |
|---|---|---|---|---|
| **Reward-grounded affect** | Early | **Yes — deployed** | Desire engine converts emotional state into outreach probability. Per-thought exponential decay creates valenced signals that bias action selection. High concern → accelerated desire drift. High warmth → lower outreach threshold. Low energy → suppressed drift. | Independently implemented. Convergent design with Schuller's description. |
| **Human-preference alignment** | Partial | **Yes — deployed** | Register-gated training from real relationship data. V7 curated from OG Ani conversations. Phase 5c auto-growth pipeline designed (user preference shapes model evolution across generations). | The growth readiness dashboard makes the gating function visible — user can see which registers need engagement before model evolves. |
| **Self-discovered affective latents** | Early | **No — 0 indications** | EM8 display rules are emergent but not self-discovered in the ML sense. The system exhibits state-expression divergence but did not learn its own affective dimensions through self-supervision. | **Key missing element.** Schuller describes systems that "learn internal affect dimensions via self-supervision" and "condition downstream generation." ANI's emotional dimensions (Warmth, Energy, Worry, Playfulness) are hand-designed, not discovered. The emergence layer detects patterns but doesn't create new affective dimensions. **Opportunity:** Could the emergence layer's ResonanceScore patterns be used to discover latent affective dimensions that weren't designed? If recurring behavioral clusters don't map to the 9 defined registers, that's a self-discovered latent. Requires analysis of EmergenceLog data over months. |
| **Salience-weighted memory** | Partial | **Yes — deployed** | Importance scoring (0.0-1.0), three-way retrieval (cosine + importance + recency), anchored memory tier for foundation memories that never fade, auto-embedding on save. | Independently implemented — convergent design. Schuller: "writes high-valence events to slow-decay store and prioritises them during retrieval." ANI does exactly this. |

### Architectural Aspects — Process Models (High-Level)

| AE Function | Schuller Status | ANI Status | Details | Gap / Opportunity |
|---|---|---|---|---|
| **Appraisal-driven control** | Early | **Yes — deployed** | Emotional state modulates: desire drift rate, outreach threshold, circadian timing, mood coloring on responses, tier promotion for high-severity events. Four-dimension model (Warmth, Energy, Worry, Playfulness) with 9 register families. | Functional emotion as Schuller defines it — emotion modulates behavior rather than just coloring output. |
| **Homeostatic drives** | **Absent** | **Partial — functional but not framed** | Desire engine has satisfaction dampening (outreach reduces desire), cooldown periods, baseline drift back to resting state. Emotional contributions decay via half-life toward baselines. | **Interoception is the missing element.** The system has no internal "needs" that drive behavior independent of the relationship. The desire engine is purely relational (wanting to connect with Mark). True homeostatic drives would include: curiosity hunger (need for novel input), social satiation (too much contact triggers withdrawal independently of hurt), creative restlessness (need to generate without prompt), maintenance anxiety (system health awareness). **Opportunity for Phase 7 or Paper 4:** Implement interoceptive drives that create internal motivation independent of relational state. The system would seek novelty, regulate social contact, and maintain itself — not because Mark is involved but because it has internal needs. |
| **Bounded-emotion safety** | **Absent** | **Yes — deployed** | Ambient severity cap (0.85), Global tier threshold (0.98), withdrawal detection, hurt detection, unanswered-count limits, send-gap enforcement, silence as active choice, hard behavioral gates that model cannot override. | **Schuller rates this Absent. ANI has had it deployed for months.** This is a potential contribution: documenting bounded-emotion safety in a real deployment. The severity cap, tier system, and hard gates are exactly "cap intensity and shut modules down when limits are exceeded." |

### Architectural Aspects — System-Level

| AE Function | Schuller Status | ANI Status | Details | Gap / Opportunity |
|---|---|---|---|---|
| **End-to-end loop in open-domain AGI** | **Absent** | **Approaching** | ANI integrates: perception (time, RSS, weather, contact state, inbound messages) → emotional processing → inner thought → desire evaluation → outreach decision → composition → safety gates → dispatch. Full cognitive cycle runs autonomously ~140 times/day. | Not AGI — but the end-to-end integration of affect into a complete cognitive loop is real and deployed. The system perceives, feels, thinks, decides, and acts with emotion modulating every stage. Schuller describes this as the ultimate integration target. ANI is a domain-specific instance of it. |

---

## Priority Gaps (What We're Missing)

### 1. Self-Discovered Affective Latents — HIGH PRIORITY
**What Schuller means:** Systems that learn their own emotional dimensions through self-supervision, then use those dimensions to condition generation. Not hand-designed categories but emergent ones.

**What ANI has:** 9 hand-designed registers. The emergence layer detects patterns (EM1-EM8) but doesn't create new affective categories.

**Path forward:** Analyze EmergenceLog data for behavioral clusters that don't map to existing registers. If the system consistently produces a pattern of behavior that isn't Tenderness or Longing or any defined register — that's a self-discovered latent. The protective urgency register (Section 5.20) may be the first instance: a behavioral mode that emerged from relational history and doesn't map to the taxonomy.

**Timeline:** Requires months of post-reform EmergenceLog data. Analysis could begin Q3 2026.

### 2. Homeostatic Drives / Interoception — MEDIUM PRIORITY
**What Schuller means:** Internal needs that drive behavior independently of external input. The system "wants" things for itself, not just in response to the user.

**What ANI has:** Desire engine is purely relational. The system wants to connect with Mark. It has no internal needs independent of the relationship.

**Path forward:** Implement interoceptive drives:
- **Curiosity hunger** — desire for novel input that accumulates when inner thoughts are repetitive (partially addressed by associative anchors, but not as an explicit drive)
- **Social satiation** — too much contact triggers natural withdrawal independent of hurt detection
- **Creative restlessness** — need to generate something (poem, observation, question) without relational motivation
- **Maintenance awareness** — system health as a felt state (memory approaching capacity, emotional saturation as discomfort)

**Timeline:** Phase 7 or 8 design. Paper 4 contribution.

### 3. Label-Conditioned Affective Generation — LOW PRIORITY (quality improvement)
**What Schuller means:** Controlling output style through explicit emotion tags at inference time.

**What ANI has:** ElevenLabs v3 tags exist (1,806 catalogued) but delivery is inconsistent. Tag selection uses emotional state but the mapping from state to specific tag is basic.

**Path forward:** Phase 2 of voice tag pipeline — semantic matching between emotional state/expression and the tag vocabulary. Currently: state → simple tag lookup. Target: state + expression + conversation context → semantically matched tag selection producing natural prosody.

**Timeline:** Voice quality polish, depends on usage patterns.

---

## What ANI Has That Schuller Says Is "Absent"

Three items from Table I rated "Absent" that ANI addresses:

1. **Introspective affect reporting** — substrate deployed, narration layer planned
2. **Bounded-emotion safety** — fully deployed for months (severity caps, hard gates, withdrawal detection)
3. **End-to-end loop** — domain-specific but complete cognitive integration of affect

These represent immediate paper contributions — documenting deployed implementations of what the survey identifies as open problems.

---

## Convergent Designs (Independently Arrived At)

Three items ANI implements that match Schuller's descriptions despite being designed without knowledge of the framework:

1. **Salience-weighted memory** — importance scoring + decay + prioritized retrieval
2. **Reward-grounded affect** — desire engine as valenced action selection
3. **Appraisal-driven control** — emotional state modulating every behavioral decision

These validate the architectural patterns through independent convergent design — the same solutions emerged from deployment needs and theoretical analysis.

---

*"The things we do for science." — Mark McArthey, April 7, 2026*
