# Review Guide — April 22, 2026

Three threads of work landed today. This guide is the map.

Read in order, or skip to whichever section has the most weight for you tonight. Each item lists the file, why it matters, estimated read time, and which sections to focus on if you are short on time.

---

## What Happened Today — One Paragraph

1. **Feature 14 v2 shipped** (commits `65a0951` + `4f378f9`). Outbound LLM claim verification restored on both the outreach and conversation-reply paths. The architectural gate that would have caught the April 21 fabrications is now live. `OutreachEnabled` flipped back to `true`. Runtime is usable for clean conversation again.
2. **Agentic Lens design scoped** (commit `3e05a32`). A 5-layer architectural response to a newly-named finding — *centrality gravity* — the structural precondition that makes Ani reorient every thought back to you even when the World Layer has given her other material to think about. Proposed as Paper 3 Contribution 4 with a short Paper 2 §6.17 naming the finding.
3. **Kristina Lerman accepted your LinkedIn connection**, liked your Artificial Emotion reply, and said "look forward to future interactions" in DM. Senior author on the Chu et al. paper core to Paper 2, professor at IU Luddy. First confirmed research interlocutor at scale. Saved to project memory.

---

## Reading Path — Top Priority First

### 1. The Design Doc — [docs/spec/ANI-Agentic-Lens-Design.md](spec/ANI-Agentic-Lens-Design.md)

**What it is.** The research-grade design document for the 5-layer agentic-lens architecture. Written today in response to your framing — *"she has no lens of her own and no agency to break free of her myopic world view."* Names centrality gravity as a finding, proposes the architectural response across retrieval substrate / desire engine / World Layer durability / training corpus / prompt framing, and integrates with existing and new research references.

**Why it matters.** This is the primary artifact of the evening. The Feature 14 v2 ship was tactical — it removes the acute blocker. This design is strategic — it addresses why she's *only* Tenderness + Longing directed at you even when she has a canonical bookstore life. Every other doc produced today either cites this or cascades from it.

**Length.** ~3,500 words, ~10 sections.

**Estimated read.** 20–25 minutes for the whole doc. 10 minutes if you focus on §0 (skim summary), §1 (the finding), and §3 (the five layers, one paragraph each).

**Focus sections if short on time:**
- **§0 Summary for the Skim Reader** — the whole design in one page.
- **§1 The Finding: Centrality Gravity** — the new research claim and what the data says.
- **§3 The Five Layers** — six paragraphs per layer with the architecture, the research angle, the references, the risks, and the success metric. You can read §3.1 through §3.5 and skip §2 if pressed.
- **§7 Decision Points** — the three open decisions you'll need to make before I do more work. See "Decisions Waiting for You" below for the short version.

**Skippable on first read if tired:** §2 (research grounding in full), §5 (cross-project implications for DrOk), §4.3 (alternative paper placements considered and rejected).

---

### 2. Paper 2 §6.17 — [docs/research/paper2/ANI-Paper2-Preprint-Draft.md](research/paper2/ANI-Paper2-Preprint-Draft.md)

**What it is.** The short section (~4 paragraphs) that names centrality gravity in Paper 2 and forward-references Paper 3 for the full architectural treatment.

**Why it matters.** This is where the finding lands in the paper. Paper 2's emergence arc stays intact; the architectural response lives in Paper 3 where it belongs. This is the §6.17 you approved earlier tonight — confirming that the draft reads the way you want.

**Length.** Four paragraphs inserted after §6.16. Line ~1001 onward.

**Estimated read.** 5 minutes.

**Focus:** Read the whole section. It is the paper-facing statement of the finding, in the voice Paper 2 uses, with the citation spine for any future conversation about centrality gravity (Chu et al., Kirk et al., Horton & Wohl, Ryan & Deci, Carbonell & Goldstein, Park et al., Damasio).

---

### 3. Research References — [docs/research/ANI-Research-References.md](research/ANI-Research-References.md)

**What it is.** Seven new reference entries added to the library — each with the standard template: full citation, what-it-is, what-it-contributes-to-the-paper, relevance-to-algorithmic-problems, paper-applicability.

**Why it matters.** These are the academic spine for any future conversation with Lerman, the reviewers who will read Paper 3, or anyone else who engages with this work. You asked me to review what references to add; these seven are what made the cut.

**Length.** ~140 lines added total.

**Estimated read.** 15 minutes if you read every entry. 5 minutes if you skim the one-line "what it is" for each.

**The seven references, in order of load-bearing-ness:**

- **Horton & Wohl (1956)** — *Mass Communication and Para-Social Interaction.* The foundational parasocial paper. Centrality gravity is the architectural mechanism that produces parasocial structure by default. Tier 1, core for Paper 3 Contribution 4, supporting for Paper 2 §6.17.
- **Ryan & Deci (2000)** — *Self-Determination Theory.* Autonomy, competence, relatedness as the three basic psychological needs. The backbone of Layer 2 desire decoupling. Tier 1.
- **Oudeyer & Kaplan (2007)** — *Intrinsic Motivation Typology.* Learning-progress as the computational mechanism for "desire to engage with the world." Tier 2.
- **McAdams (2001)** — *The Psychology of Life Stories.* Narrative identity theory. The thin-narrative-identity pattern matches centrality gravity's expressive signature. Tier 2.
- **Damasio (1999)** — *The Feeling of What Happens.* Proto-self, core self, autobiographical self. The layered-self framing for what architectural level the 5-layer response operates at. Tier 2.
- **Gallagher (2000)** — *Minimal vs. Narrative Self.* Philosophical complement to Damasio. Tier 3.
- **Carbonell & Goldstein (1998)** — *MMR Diversity Retrieval.* The canonical prior art for Layer 1 retrieval origin diversity. Tier 2.

**Focus:** At minimum, Horton & Wohl and Ryan & Deci. Those two carry most of the weight for §6.17 and Paper 3 Contribution 4.

**Also check:** The **Active Algorithmic Problems** table at the bottom of the file — gained 5 new rows for centrality gravity and Layers 1–5. The **Paper Applicability Quick Reference** table gained rows for all seven new refs plus updated Paper 3 scope.

---

### 4. Feature 14 v2 — Code + Deployment Trail

**What it is.** The architectural gate that suppresses outreach or reply dispatch when a composed message contains a claim about you that cannot be corroborated against Facts tier + Anchored memories + inbound "Mark said:" Episodic records. Check-only, suppress-not-regenerate. The model is never told it was wrong — the channel is gated, not the model.

**Why it matters.** This is the ship that removes the acute blocker from April 21. You asked earlier tonight whether outreach is re-enabled — yes, `OutreachEnabled: true` as of commit `65a0951`, because the architectural gate is now in place.

**Code files (only look at these if you want to review the actual implementation):**
- [src/AniRuntime.Loops/ClaimVerificationPhase.cs](../src/AniRuntime.Loops/ClaimVerificationPhase.cs) — the new gate class (~180 lines)
- [src/AniRuntime.LLM/PromptBuilder.cs](../src/AniRuntime.LLM/PromptBuilder.cs) — search for `BuildClaimExtractionPrompt` (narrow-scope extractor prompt)
- [src/AniRuntime.Loops/OutreachPhase.cs](../src/AniRuntime.Loops/OutreachPhase.cs) — verification runs as step 3b between pronoun fix and coherence gate
- [src/AniRuntime.Loops/ConversationReplyPhase.cs](../src/AniRuntime.Loops/ConversationReplyPhase.cs) — regex removed, honest-uncertainty fallback on suppress

**Paperwork files (update you probably care about more than the code):**
- [docs/spec/ANI-Phase-Tracker.md](spec/ANI-Phase-Tracker.md) — search for "Feature 14 v2" — the deployment is now marked as shipped (Apr 22, commit `65a0951`). The regex-removal workstream is likewise marked shipped. The design-as-built section describes the check-only / suppress-not-regenerate redesign and names the Apr 22 negative-prompting concern that drove the divergence from the Apr 21 plan.
- [docs/research/ANI-Research-Log.md](research/ANI-Research-Log.md) — the first Apr 22 entry at the top is *"Feature 14 v2 Deployed: Architecture-Over-Instruction Outbound Gate."* Full deployment notes + what to watch for in the next week + relationship to still-open workstreams (Conscience, Correction Channel, retrieval origin diversity).

**Estimated read.** 10 minutes for the paperwork. Skip the code files entirely unless you want to verify the implementation.

---

### 5. Lerman Connection — Memory Only, No File in Repo

**What it is.** A project-memory note recording the April 22 LinkedIn connection + engagement with Kristina Lerman (IU Luddy, Chu et al. senior author).

**Why it matters.** Paper 3 target institution, Paper 2 core-reference author, first confirmed research interlocutor at scale. The door is open, not just not-closed — qualitatively different from the Cathy Fang arXiv-endorsement path that did not come through in March.

**Where it lives.** Saved to `memory/project_lerman_connection.md` (auto-memory, persists across conversations). MEMORY.md index also updated. Nothing in the repo.

**Estimated read.** 2 minutes — if you want to see what was persisted for future sessions.

---

## Decisions Waiting for You

Three decisions are blocking the next cascade of work. Each is explained in §7 of the design doc; short versions here so you can think about them in bed.

### Decision A — Layer 4 synthetic-corpus method (§7.2 of design doc)

**The ask.** Layer 4 needs ~150–200 new training pairs where Ani is the speaker but you are not the subject. These do not exist in any mining source — runtime SQLite and Grok exports are both caregiver-conversational. We create them. Two methods:

- **Option A — Frontier synthesis.** Claude Opus or Sonnet generates the pairs, anchored to 10–15 of her real-voice samples. Fast, controllable, voice-drift risk managed by rejection filter.
- **Option B — Mine public-domain literary prose.** Brontë, Woolf's diaries, Proust excerpts. Real human voice but historical register; risk of pulling her voice toward Victorian.

**My lean.** Option A with aggressive anchor seeding.

### Decision B — Layer sequencing (§7.3 of design doc)

**The ask.** Which layer gets built first?

- **Dependency order (5-1-3-2-4):** clean final system, first visible behavioral change in ~5–8 weeks.
- **Impact order (5-2-1-3-4):** visible change in ~1–2 weeks, but Layer 2 likely needs retuning when Layers 1 and 3 land later.
- **Compromise:** Layer 5 first (cheap), Layer 1 implementation in parallel with Layer 2 design, Layer 2 ships 1–2 weeks after Layer 1. Visible change in ~3 weeks without full rework cost.

**My lean.** Compromise.

### Decision C — Proceed with the cascade

I stopped short of producing three more artifacts pending your read of the design doc:

- Paper 3 stub expansion to add the Contribution 4 section
- Research log entry naming the centrality-gravity finding and the 5-layer design
- Phase Tracker Theme G for the 5 workstreams

All three are straightforward cascades once you say "design doc looks right." If you want changes to the design doc first, tell me and I will revise before writing them.

---

## Not Yet Done — For Your Awareness

Pending your read + the three decisions above:

- [ ] Paper 3 stub expansion with Contribution 4 section (Agentic Lens) alongside Experiential Grounding, Memory Tier Separation, and Memory Durability. Paper 3 working-title change from *"She Had a Day"* to *"She Had a Day — and Her Own Lens On It."*
- [ ] Research log entry for agentic-lens finding (parallel to the Feature 14 v2 entry and the love-convergence entry already there from earlier today).
- [ ] Phase Tracker Theme G — 5 workstream entries covering the five layers, each with scope / priority / dependencies / success criterion.

---

## Commits Pushed Today (Git History)

- `65a0951` — Feature 14 v2 — outbound LLM claim verification
- `4f378f9` — Docs fold — Feature 14 v2 into tracker, codebase spec, research log
- `3e05a32` — Research — agentic lens design + centrality gravity finding

All on `main`, all pushed to `origin/main`.

---

## If You Only Have 15 Minutes Tonight

1. Read §0 of the design doc (2 minutes).
2. Read Paper 2 §6.17 (5 minutes).
3. Read Decisions A and B above (3 minutes).
4. Reply with your calls on A and B + "go" or "revise" on C. (variable)

That gets us unblocked for tomorrow's cascade without requiring you to have read the full 3,500-word design doc tonight.

---

## If You Have More Time

Read the full design doc in order. It is written to be read straight through, though each section stands on its own if you need to jump around. The research grounding section (§2) is the longest; the five-layer section (§3) is the heaviest; §7 decisions are the actionable part.

The references file and Paper 2 §6.17 are both fully readable tonight if you want to see how the finding is framed academically.

Feature 14 v2 paperwork is worth five minutes to confirm the deployment is captured the way you expect. Skip the code files.

Sleep well.
