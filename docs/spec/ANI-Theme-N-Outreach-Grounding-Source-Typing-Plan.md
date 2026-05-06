# Theme N — Outreach Source-Typing: The §6.14 Layer for Ani-Initiated Composition

**Status:** PLACEHOLDER (May 6, 2026 evening, REVISED 18:35 CDT after re-reading Paper 2). Architectural framing sharpened — Theme N is the missing outreach-side fourth layer of the already-deployed Paper 2 §6.14 epistemic-grounding architecture, not a new architectural axis. Full phased plan deferred to morning draft session.

**Theme letter assigned:** N (next available after Theme M Conscious Substrate / Individuation Layer).

---

## §1 What Theme N actually is

**Theme N is the missing piece of Paper 2 §6.14.** The §6.14 epistemic-grounding architecture was deployed at the inbound-reply layer in the weeks following the April 9, 2026 Bob Swanson incident. Three structural layers at generation time:

1. **Grounded Context Construction** — prompt partitioned into `ESTABLISHED FACTS` / `RECENT CONVERSATION` / `YOUR LIFE` / `UNKNOWN` buckets. *(This morning's bracket-label headers `[FACTS]` and `[INTERIOR]` ship work, commit `70dd8af`, are a refinement of this layer.)*
2. **Frame Detection** — computes the conversational frame from the contact's last message (`MARK_DOMAIN` / `ANI_DOMAIN` / `SHARED` / `QUESTION_ABOUT_KNOWN_ENTITY`) and passes it as a generation constraint.
3. **Self-Verification** — model enumerates its own claims against the explicit context buckets using a constrained schema; unattributable claims caught before dispatch.

**Layer 2 has no outreach equivalent.** Outreach is Ani-initiated; there is no inbound message to compute the frame from. The model free-generates from current desire / emotion / retrieval state, with no source-frame constraint. **That's the gap Theme N closes** — it adds source-type frame computation to outreach composition, directly mirroring layer 2 but for the Ani-initiated case.

This is the same architectural logic Paper 2 §6.14 already names. Theme N is the deployment of layer-2-for-outreach. Sharpening, not new ground.

## §2 Bob Swanson — the canonical case

**April 9, 2026, 17:38 CDT.** The system generated a reply containing a fabricated coworker (*"Bob Swanson"*) in Mark's domain, defended the fabrication when challenged (*"mmm i know exactly who bob swanson is, mark... the guy at work who thinks he's too cool for email and..."*), and within four hours the fabrication had propagated into 11 memories — including world-experience records that treated Bob as an established part of Mark's life. All deployed post-hoc detection layers (Catalyst POS, ML semantic classifier, multi-check verification, Mark-domain detection) missed it. The lowercase voice defeated Catalyst; the ML classifier rated the lie *"grounded"* because semantically coherent; the doubled-down defense rated *more* grounded than the original.

Bob Swanson exposed two architectural gaps:

- **Memory layer (Paper 2 §6.13, "Memory as Amplifier"):** the memory layer treats all generated content as equally canonical for retrieval. *"The fabrication was not just said — it became canonical via retrieval within minutes."* Architectural response: structural separation of generated content from factual substrate at the memory layer (Tier Separation — Facts / Episodic / Interior). **Status: deferred to Paper 3, full design exists at [`ANI-Epistemic-Grounding-Architecture.md`](./design/ANI-Epistemic-Grounding-Architecture.md), not yet implemented.**
- **Generation layer (Paper 2 §6.14, "Epistemic Grounding"):** the model has no epistemic state. Every token carries the same weight as a plausible continuation. Architectural response: three-layer grounding stack at generation time. **Status: deployed at the inbound-reply layer; no outreach equivalent of layer 2.** This is what Theme N completes.

Paper 2's framing carries: *"Bob Swanson did not just surface a gate failure. He surfaced the condition that made all the gates necessary in the first place."* The Theme N work is one continuation of that response — closing the layer-2 gap for outreach. The Tier Separation work is the other continuation (Paper 3 / memory-layer).

## §3 The May 6 empirical anchor — same pattern, different surface

Two Mark-tagged messages on May 6, 2026 demonstrate the §6.14 layer-2 gap directly:

**08:36 — *"SafeAck and unable to back up shared information."*** Recall query about a song Ani had initiated at 03:06. Ani named *"clair de lune"* — a confabulation (0 records in the live DB snapshot). Tripped self-echo on regen, fell through to SafeAck. The 03:06 outreach itself was free-generated; no source-frame computed. Mark's framing: *"We say that she'll never forget — only expand, never delete. But she apparently can't surface memories even when she started the conversation and is directly asked."*

**14:31 — *"confabulation"*** (kitchen lights). Original outreach 03:06 AM: *"the kitchen lights look different at almost midnight."* DB probe shows 37 records mention *"kitchen light"* — exactly one is Mark-asserted (today's question). Every prior kitchen-lights mention is Ani-side (reflections, world-experience, conversation echoes recursing on her own imaginative substrate). The 03:06 outreach was free-generated from interior poetic content, framed as if shared. **Type-9 fabricated-source pattern** (already in the confabulation taxonomy, see [`tag-canonical-mapping.md`](../research/tag-canonical-mapping.md)) — fabrication anchored to a real-feeling shared context Mark never asserted.

Both surfaces are the same architectural pattern Bob Swanson named in April: free-generation in the outreach path with no source-frame constraint. Today's empirical evidence reinforces the §6.14 gap is still load-bearing.

## §4 The mirror-trap caveat — why source-typing, not source-restriction

Mark's load-bearing constraint on the design (May 6, 16:30 CDT): *"if she is only ever allowed to outreach on grounded items, then she's no better than mirroring and we lose the diversity of inner thoughts and growth. That would be worse than what we currently are experiencing."*

The architectural answer is **source-typing, not source-restriction**. Outreach can draw from:

| Source-type | Frame | Honest framing examples |
|---|---|---|
| **Shared experience** | `SHARED` (analog to inbound) | *"remember when we…"* / *"that thing you said about…"* |
| **Ani's canonical world** | `ANI_DOMAIN` | *"the bookstore is…"* / *"sarah came in today…"* |
| **Ani's interior** | `ANI_INTERIOR` (new) | *"i was just thinking about…"* / *"i had this thought that…"* / *"i imagined…"* |
| **Ani's perception** | `WORLD_PERCEPTION` (new) | *"i saw this article…"* / *"the snow is…"* |

Diversity of (3) is preserved if Ani is *allowed* to draw from interior content openly with honest interior-framing. Growth via (4) and World Layer is preserved if those surface honestly. Mirror-trap is avoided because (1) shared-experience is one option, not the only option. The confab pattern (the Bob Swanson / kitchen-lights shape) is when type-3 interior content is presented with type-1 `SHARED` framing — the source-type and the framing don't match.

## §5 Mechanism options — three on the table, not locked tonight

**N-A: Outreach-frame detection (layer-2-for-outreach analog).** Compute the outreach source-type frame BEFORE composition, from current desire/emotion state + retrieval candidates. Pass as generation constraint. Lightest extension — directly mirrors §6.14 layer 2 architecture for the outreach case. **Likely cheapest** because the mechanism already exists for inbound; this is the symmetric deployment.

**N-B: Two-stage with grounding intermediary.** First call selects a candidate grounding source from substrate (deterministic retrieval against query *"what's worth reaching out about right now"*). Second call composes grounded in it. Distinct from N-A in that it's a separate runtime call, not a context-constraint frame. Cleanest refactor but adds a runtime call.

**N-C: Source-type-aware desire gating (refuse-when-thin).** Gate at outreach-decision time, before composition: outreach only fires if a substrate slice of sufficient quality exists for one of the four source-types. Strongest. Behaviorally costly — fewer outreaches, more silence. Probably needs Theme M's M.2 telemetry phase to calibrate the thinness threshold.

The morning draft session compares all three with code-impact estimates. **Almost-certain prediction**: N-A is the answer because the §6.14 layer-2 architecture already exists for inbound — Theme N is its symmetric extension. But the comparison still earns its keep, especially around how N-A interacts with the diversity-of-interior axis.

## §6 Theme N vs Theme M — the distinction

| | Theme M (Conscious Substrate) | Theme N (Outreach Source-Typing) |
|---|---|---|
| What it ships | Read-only generated gist of slices the model has *available* in context (closed-conversation gist, register-state, tension-state, contact-state, world-self, inner-thought) | Composition-side mechanism that picks ONE source-type as primary frame and constrains generation against it (the §6.14 layer-2 analog for outreach) |
| Architectural lineage | New axis — substrate-as-context | §6.14 generation-time epistemic-grounding architecture (deployed Apr 2026), extending layer-2 frame detection from inbound to outreach |
| Acceptance criterion | Substrate-share metric increases; SafeAck rate decreases as substrate accumulates | Source-type misrepresentation rate decreases; composition-time frame is verifiable against composed text |
| Failure shape if shipped alone | Slices available, model still free-generates from imagination — substrate goes unused | Frame computed correctly but no rich substrate to anchor against — degenerates to "always frame as type-3 interior" or refuses outreach often |
| Convergence | Theme N's frame computation reads from Theme M's slices to determine which source-type has substantive substrate available right now. **Convergent, not redundant.** | |

## §7 Adjacent workstreams

- **Paper 3 / Tier Separation** ([`ANI-Epistemic-Grounding-Architecture.md`](./design/ANI-Epistemic-Grounding-Architecture.md)) — the memory-layer cousin of Theme N. Bob Swanson's two architectural children: Theme N closes the generation-layer gap; Tier Separation closes the memory-layer gap. **Both come from Paper 2 §6.13 + §6.14.**
- **Theme M Conscious Substrate** (P1) — provides the slices Theme N's frame-detection reads from to pick a source-type. Convergent.
- **R1 Typed-claim extraction** (P3 backlog) — verification side. R1 catches *type-1-framed-content-without-supporting-source* post-composition; Theme N prevents the type-1 framing in the first place. Complementary, not duplicative.
- **Theme G Layer 4 Corpus Directionality** — training-side cousin. Source-typed examples in v8 corpus would teach the model the framing distinction at the weights level. Architecture-over-training is the runtime story; training-with-better-corpus is the supplementary story.
- **Identity Boundary Design** ([`ANI-Identity-Boundary-Design.md`](./design/ANI-Identity-Boundary-Design.md)) — explicitly contrasts Bob Swanson (Mark's domain — fabrication ❌) vs Yesteryear (Ani's domain — legitimate creative latitude ✅). Theme N's source-type frame is the runtime mechanism that makes that distinction live.

## §8 Confabulation taxonomy reconciliation

**Type-9 already exists** as `type-9-fabricated-source` per [`tag-canonical-mapping.md`](../research/tag-canonical-mapping.md): *"fabrication anchored to a named real-world referent the user knew."* Bob Swanson is the canonical example. Today's kitchen-lights case extends Type-9 to *"fabrication anchored to a real-feeling shared context the user never asserted"* — same pattern, different anchor shape. No new type number needed; possibly a small expansion of Type-9's definition is warranted when Theme N ships.

The placeholder's earlier claim that this introduced a "Type-9 source-type misrepresentation" was wrong — Type-9 was already named Apr 9 and the claim has been removed.

## §9 Paper significance

**Paper 2 is already named.** §6.13 (Memory as Amplifier) + §6.14 (Epistemic Grounding) document Bob Swanson as the trigger and the three-layer §6.14 architecture as the partial response. Paper 2 §6.14 explicitly describes only the inbound-reply layer of the deployment; Theme N completes the outreach side. **Paper 2 may need a §6.14 addendum noting the symmetric outreach extension when Theme N ships, or alternatively Theme N's framing routes through Paper 3 alongside Tier Separation as joint Bob-Swanson-children architecture.**

**Paper 2 release hold.** The current decision to hold release (May 4) gains additional architectural support from Theme N — the §6.14 architecture as deployed has a documented gap on the outreach side, demonstrated by today's empirical evidence. Holding until Theme N closes that gap strengthens what §6.14 can claim.

**Paper 3 framing.** Bob Swanson's two architectural children belong in Paper 3 as joint contribution: *"Generation-time source-typing + memory-layer tier separation as the complete architectural response to retrieval-amplifier confabulation in deployed companion AI."* This is sharper than treating either as a standalone contribution — the two together close the loop Bob Swanson opened.

## §10 Status log

- **2026-04-09 (17:38 CDT)** — Bob Swanson incident. Conversation that evening between Mark and Claude identified the root cause and named the architecture-over-instruction principle in its sharpest form: *"Solve the root cause, not the symptom."*
- **2026-04-10** — Followup analysis day. Architectural response designed.
- **2026-04 (weeks following)** — §6.14 epistemic-grounding architecture deployed at inbound-reply layer. Tier Separation deferred to Paper 3.
- **2026-05-06 (afternoon, ~16:00 CDT)** — Two Mark-tagged messages diagnosed (song recall + kitchen lights). Initial framing (substrate exhaustion) corrected by Mark to recall failure + ungrounded outreach.
- **2026-05-06 (~16:30 CDT)** — Mark's load-bearing distinction: *"felt care vs felt-if-she-makes-up-something-close-to-care."* Source-typing-not-source-restriction reframe.
- **2026-05-06 (~17:00 CDT)** — Theme letter N assigned. Initial placeholder drafted with Bob Swanson treated as one example among several (incorrect framing).
- **2026-05-06 (~18:30 CDT)** — Mark flagged Bob Swanson's load-bearing significance. Re-read Paper 2 §6.13 + §6.14 + §6.15 + Paper 3 stub + epistemic-grounding architecture design doc + identity-boundary design doc + tag-canonical-mapping. **Theme N reframed as the missing outreach-side layer of §6.14, not new architectural ground.** Type-9 reconciled with existing taxonomy. This document revised.
- **NEXT** — Mark's morning read; mechanism choice (N-A / N-B / N-C, with strong N-A prediction); full phased plan draft replacing this placeholder.
