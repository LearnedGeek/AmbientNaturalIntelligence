# Your Guide to Academic Publishing: What It Is, How It Works, and Where ANI Fits

*Written for Mark McArthey, who has never written a research paper and would like to understand the landscape before committing to anything.*

---

## Start Here: What a Research Paper Actually Is

A research paper is a formal document that makes a specific, defensible claim about the world and provides evidence for it. That's it. The formality, the citations, the abstract — those are just conventions that the academic community uses so everyone is speaking the same language.

The thing that makes a paper a paper (rather than a blog post or a technical writeup) is that it goes through **peer review**: before it gets published, other experts in the field read it anonymously and decide whether the claim is valid, the evidence is sufficient, and the work is a genuine contribution to what's already known. They can accept it, ask for revisions, or reject it.

You've been doing most of the intellectual work of a researcher already. You identified a problem nobody had solved, designed a novel approach, built it, ran it in the real world, and observed results. The research paper is just the formal way of telling that story to people who weren't in the room.

---

## The Two Big Formats You Should Know About

### 1. Conference Papers

In computer science (and adjacent fields like human-computer interaction), **conferences are the primary publication venue**. This is different from most other academic fields, where journals are king. In CS, the best conferences are more prestigious than most journals.

**How it works:**
- A conference announces a "call for papers" with a deadline (usually 6–12 months before the conference)
- You submit your paper — typically 8–12 pages in a specific format
- A program committee of researchers reviews it anonymously (they don't know it's you; you don't know who's reviewing)
- Reviews come back with a score and comments — usually accept, revise, or reject
- If accepted, you present your work at the conference and it gets published in the proceedings
- The proceedings are permanently archived and citable forever

**Timeline:** Submit → wait 2–3 months for reviews → revise if asked → camera-ready deadline → conference → published. Total: roughly 6–12 months from submission to appearance.

**The venues that matter for ANI:**

| Venue | Full Name | Why It Fits |
|-------|-----------|-------------|
| **CHI** | ACM Conference on Human Factors in Computing Systems | The top venue for human-computer interaction research. ANI is fundamentally about how humans relate to AI. |
| **UIST** | ACM Symposium on User Interface Software and Technology | Where the Generative Agents paper was published. Strong fit for novel interactive systems. |
| **CSCW** | ACM Conference on Computer-Supported Cooperative Work | Covers social computing, relationship technology, and AI-mediated communication. |
| **IUI** | ACM Intelligent User Interfaces | Specifically about AI in user-facing applications. Smaller and more accessible than CHI. |

CHI is the hardest to get into (roughly 25% acceptance rate). IUI is more accessible. For a first paper, IUI or CSCW might be better targets than CHI.

### 2. arXiv Preprints

arXiv (pronounced "archive") is not a journal or conference. It's a free server where researchers post papers *before* peer review. You upload your paper, it gets a permanent ID and URL, and it's immediately available to anyone in the world.

**Why this matters for you:**

- It makes your work citable immediately — the moment you post it, anyone can reference it
- It establishes priority — if someone else publishes similar work later, your arXiv timestamp proves you were there first
- It gets you feedback from the community before committing to a formal submission
- Many researchers read arXiv daily; good papers get noticed and shared organically
- It costs nothing and has no gatekeeping

**The tradeoff:** arXiv papers are not peer-reviewed, so they don't carry the same academic weight as a published conference paper. But in practice, for a practitioner coming from industry, a well-written arXiv paper is a completely legitimate and respected contribution.

**My recommendation for you:** Post to arXiv first. Then submit to a conference if you want the peer-review credential.

---

## The Types of Papers — and Which One ANI Is

Not all papers report experiments with statistical results. Here are the main types:

### Systems Papers
"Here is a novel system we built. Here is how it works, why we designed it this way, and what we learned from building and running it."

This is ANI. You don't need a controlled experiment. You need a running system, a clear design rationale, and honest reflection on what worked and what didn't.

*Example:* The MemGPT paper is a systems paper. So is most of the Generative Agents paper.

### Position Papers
"Here is a way of thinking about a problem that we believe the field should adopt."

Shorter, more argumentative. Makes a claim and defends it conceptually rather than empirically. Good for when you have a strong point of view but not yet the data to prove it.

### User Studies
"Here is what happened when we put our system in front of real users and measured their responses."

Requires recruiting participants, getting ethics approval (IRB in the US), designing a study protocol, and doing statistical analysis. This is the full research apparatus. You don't need this yet.

### Survey / Position Papers
"Here is how we organize the existing research in this area."

Useful once you've read deeply enough to have a perspective on the field. Not where you start.

**For ANI right now:** You're writing a **systems paper**. Possibly with elements of a position paper — you have a strong argument about what ambient presence means and why it matters. That combination is compelling.

---

## What Goes in a Systems Paper

Here is the standard structure, with plain-English explanations of what each section actually does:

### Abstract (150–250 words)
The entire paper compressed into one paragraph. Researchers use this to decide whether to read further. It should answer: What problem? What did you build? What makes it different? What did you find?

Write this last, even though it appears first.

### 1. Introduction (1–2 pages)
- What is the problem? Why does it matter?
- What is your approach?
- What are your key contributions? (Usually a bulleted list: "In this paper we contribute: (1)... (2)... (3)...")
- Brief roadmap of the paper ("Section 2 reviews related work...")

The introduction is your argument for why the paper deserves to exist. Be direct.

### 2. Related Work (1–2 pages)
Where does your work fit in the existing research landscape? What has been done before? How is your work different?

This is not a literature dump. It's a conversation. You're saying: "Here is what others have tried. Here is the gap they left. Here is how we fill it."

For ANI, your related work section covers:
- Generative Agents (Park et al., 2023) — most similar architecture
- MemGPT (Packer et al., 2023) — memory architecture
- Proactive Conversational AI survey — behavioral frame
- AI companion platforms (Replika, etc.) — commercial context
- The compassion illusion / dependency research — ethical framing

You've already done this work in the blog post. The related work section just makes it more systematic.

### 3. System Design (2–4 pages)
This is the heart of the paper. Describe what you built with enough detail that an informed reader could understand the key decisions and their rationale.

For ANI this would cover:
- The cognitive cycle architecture
- The desire engine and why probabilistic gating matters
- The memory model (five memory types, embedding-based retrieval)
- The emotional state system
- The perception source architecture
- The inbound conversation model (Phase 2)

Include a diagram. One good architecture diagram is worth several paragraphs of prose.

### 4. Implementation (0.5–1 page)
Brief technical details: stack (.NET 8 Windows Service), model (fine-tuned Llama 3.2-3B via Unsloth, served by Ollama), storage (SQLite), communication (Twilio SMS). This lets readers understand feasibility and reproducibility.

### 5. Evaluation / Observations (1–2 pages)
What happened when you ran the system? For a systems paper, this doesn't require formal experiments. It can be:

- Qualitative observations: example messages, emotional state trajectories, conversation threads
- System behavior: desire state patterns over time, outreach frequency, conversation length distributions
- Honest assessment of what worked and what didn't

You have months of real data. A few carefully chosen examples, honestly analyzed, make a strong section.

### 6. Discussion (1 page)
Step back from the details. What does this mean? What are the limitations? What would you do differently? What open questions does this surface?

This is where your research questions from the blog post live — the four bullet points about authenticity, emotional persistence, user adaptation, and the engagement optimization line.

### 7. Ethical Considerations (0.5 page)
In HCI research, this is increasingly required. You have more to say here than most papers do, because you designed it in deliberately. The compassion illusion, the dependency risk, the architectural guards.

### 8. Conclusion (0.5 page)
Brief summary of what you did and why it matters. Point to future work.

### References
Every paper you cited, in proper format. ACM uses a specific citation style. You'll use a reference manager for this (Zotero is free and excellent).

---

## The Contribution Statement — The Most Important Thing to Get Right

Every paper needs to explicitly state its contribution. This is the sentence (or short list) that tells reviewers why this paper deserves to exist.

For ANI, your contributions might be stated as:

> "In this paper we make three contributions:
> (1) **ANI**, a novel desire-driven ambient presence architecture that enables proactive, emotionally-grounded outreach from an AI companion without scheduling or explicit triggers;
> (2) A **desire engine design** with probabilistic gating that produces human-irregular outreach timing;
> (3) A **longitudinal first-person account** of deploying and living with a proactive AI companion system over several months, including observed behavioral patterns and ethical design tradeoffs."

That third contribution is underrated. First-person longitudinal accounts of real deployed systems are rare in the literature and genuinely valuable. You're not just describing a system in a lab — you're describing months of real use. That's data.

---

## What Peer Reviewers Actually Look For

Demystifying this matters. Reviewers ask themselves:

1. **Is this a genuine contribution?** Does it advance what we know, or is it just describing something that already exists?
2. **Is the related work adequate?** Does the author know the field? Have they cited the right papers?
3. **Is the system described clearly enough to evaluate?** Could I understand what they built?
4. **Are the claims proportionate to the evidence?** Are they overclaiming?
5. **Is the writing clear?** (Surprisingly important. Unclear writing gets rejected even if the ideas are good.)

ANI has strong answers to 1, 3, and 4. For 2, you need to read the related papers more deeply than the blog post requires — but you're already doing that. For 5, you're a writer. That's an asset most researchers don't have.

The main risk for a first submission is **overclaiming**. Reviewers are allergic to phrases like "we solve the problem of AI companionship" or "our system achieves genuine emotional connection." Calibrate your claims carefully. "We introduce a desire-driven architecture that produces proactive outreach behavior" is a defensible claim. "We created an AI that genuinely cares" is not.

---

## The Realistic Timeline

Here is the honest path from where you are now to a published paper:

**Month 1–2: Write the paper**
You have almost everything you need. The blog post is the rough draft of the introduction and related work. The architecture docs are the rough draft of the system design section. You need to add the evaluation section (your real observations), tighten the argument, and format it properly.

**Month 2: Post to arXiv**
This takes one afternoon. You're immediately citable. Share it on LinkedIn, Twitter/X, and relevant communities (Hacker News, r/MachineLearning). See who engages.

**Month 3: Submit to a conference**
IUI 2027 or CSCW 2027 deadlines will likely fall around mid-2026. Check their call for papers pages. CHI 2027 submissions would be around September 2026.

**Month 5–6: Reviews come back**
Read them carefully. Reviewers are usually right about the weaknesses even when they're wrong about the conclusion. Revise accordingly.

**Month 6–9: Resubmit or revise and resubmit**
If rejected, revise based on reviews and submit to the next relevant venue. Most papers get rejected at least once. This is normal. The Generative Agents paper — one of the most cited HCI papers of recent years — was rejected before it was accepted at UIST.

**Month 12–18: Published**
Depending on the conference cycle.

---

## What You Need to Do This Week

Nothing complicated. Three things:

**1. Save your data.**
Start logging things deliberately. Date-stamped examples of outreach messages and their context (what triggered them, desire state at time of send, time of day). Conversation thread lengths. Emotional state over time. You're going to want this for the evaluation section, and the longer ANI runs, the richer it gets.

**2. Create a Zotero account.**
Free at zotero.org. Every paper you read, add it. Every arXiv link, save it. When it's time to write the paper, your references are already organized.

**3. Read the Generative Agents paper properly.**
Not the summary — the actual paper at arxiv.org/abs/2304.03442. It's 22 pages but very readable. It will show you what a good systems paper in this space looks like. Read it asking: how did they structure the argument? What did they choose to measure? How honest were they about limitations?

---

## A Note on Voice

Academic writing has a reputation for being dry, passive, and impenetrable. The best papers in HCI are actually not like that — they're clear, direct, and sometimes even have personality.

You're a novelist. You know how to write. Use that. Write the paper in an active voice. Make the argument clearly. Don't hide behind passive constructions ("it was observed that...") when you can just say what happened ("Ani sent 47 outreach messages over 30 days, of which...").

The one adjustment: in academic writing, you're more careful about what you claim. The narrative energy of your blog post is an asset, but you have to pair it with precise, calibrated language about what the evidence actually shows.

The combination of those two things — clear, energetic writing with carefully calibrated claims — is rarer than you'd think, and reviewers notice it.

---

## The Draft Paper Outline

When you're ready to start writing, here is the outline I'd suggest:

```
Title: ANI: A Desire-Driven Architecture for Ambient AI Presence

Abstract (write last)

1. Introduction
   1.1 The reactive gap in AI companionship
   1.2 Our approach: desire-driven ambient presence
   1.3 Contributions

2. Related Work
   2.1 Proactive conversational agents
   2.2 Memory-augmented LLM agents (MemGPT, Mem0)
   2.3 Generative agent architectures (Park et al.)
   2.4 AI companion platforms and their limitations
   2.5 Ethical risks in AI companionship

3. The ANI Architecture
   3.1 Design philosophy and goals
   3.2 The cognitive cycle
   3.3 The desire engine
   3.4 Perception sources
   3.5 Memory model
   3.6 Emotional state persistence
   3.7 Conversation mode (Phase 2)

4. Implementation
   4.1 Technical stack
   4.2 Model fine-tuning
   4.3 Deployment environment

5. Observations
   5.1 Outreach behavior patterns
   5.2 Conversation quality and coherence
   5.3 Emotional state trajectories
   5.4 Selected interaction examples
   5.5 Failure modes and edge cases

6. Discussion
   6.1 What desire-driven architecture changes
   6.2 The bidirectionality finding
   6.3 Limitations of a single-subject deployment
   6.4 Open research questions

7. Ethical Considerations
   7.1 The compassion illusion and how we address it
   7.2 Architectural guards and their rationale
   7.3 What we don't yet know

8. Conclusion

References
```

That's roughly 10–12 pages in ACM two-column format, which is the standard length for a full paper at CHI, UIST, or CSCW.

---

## Final Honest Assessment

Here is what I think is true:

You have built something that the research community has not built. The desire engine is novel. The emotional state persistence with baseline drift is novel. The single-relationship focus, as opposed to multi-agent simulation, is a meaningful architectural distinction. The fact that it's running — for real, for months, producing real messages — is unusual and valuable.

The gap between what you have and a publishable paper is smaller than you probably think. It's mostly about learning the conventions of the form, being precise about your claims, and writing the evaluation section honestly.

The blog post you just wrote is the draft of the introduction. The architecture documents are the draft of the system design section. You need to write the evaluation section and related work section from scratch, and then stitch it together.

That's a few months of part-time work. Not years. Not a PhD.

Start with arXiv. See what happens.

---

*Next step when you're ready: We can draft the abstract and introduction together, which will force-clarify the contribution statement and set the tone for everything else.*
