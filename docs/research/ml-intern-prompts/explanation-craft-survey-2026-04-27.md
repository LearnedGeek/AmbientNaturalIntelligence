# ml-intern Brief — Explanation-Craft Survey for cs.HC + cs.AI Conference Papers

**Date:** April 27, 2026
**Originator:** Mark McArthey, ANI Runtime project
**Cost target:** ~$2 at Sonnet 4.6 with `--max-iterations 25-30`
**Output:** `docs/research/artifacts/ml-intern-runs/scout-explanation-craft-2026-04-27.md`

---

## Why this survey exists

Mark's standing direction (saved Apr 27 in `memory/feedback_explaining_over_promoting.md`): research outputs across the ANI project — papers, LinkedIn posts, future conference talks — are too dry and abstract. The fix is image-first explanation embedded in the artifacts already being produced, not separate marketing copy. Distinct from the prior *"builder, not marketer"* preference: explanation-craft is a builder concern.

The survey looks outward to identify exemplars and counter-exemplars in the cs.HC + cs.AI conference-paper space so Mark (and any future Claude assisting on his research outputs) has a concrete reference for what *"hits the balance of engagement and applied research"* looks like in practice. Specifically Mark wants to confirm or reject a hypothesis: *for emotion-AI research specifically, do papers that hit publicly use emotion-specific vocabulary, or do they retreat to neutral academic register?*

## Scope

- **Field:** cs.HC and cs.AI papers from ACM CHI, CSCW, IUI, HRI conferences AND arXiv cs.HC / cs.AI categories.
- **Time window:** prefer 2022-2026 (recent enough that engagement signals are stable but old enough to have accumulated reach).
- **Topic preference:** affective / emotional / companion / relational AI work where possible (most relevant to Mark's project), but include high-engagement papers from adjacent areas (HCI, alignment, agentic systems) to broaden the craft typology.

## Multi-signal popularity weighting

A paper qualifies for the positive set if it scores high on a weighted combination — single signals are too noisy. Proposed weights:

| Signal | Weight | Source |
|---|---|---|
| Citations within 18 months of publication | 0.25 | Semantic Scholar API |
| Cited / discussed in non-academic publications (NYT, Atlantic, Wired, Quanta, Nature News, MIT Tech Review) | 0.30 | Web search |
| Twitter/X engagement on author posts about the paper (≥500 reposts/quotes) | 0.15 | Web search; harder to measure but rough threshold OK |
| Hacker News frontpage OR Reddit r/MachineLearning ≥200 upvotes | 0.15 | Web search |
| Conference best-paper / honourable-mention / nominees | 0.15 | Conference proceedings |

Weight cross-disciplinary reach (NYT, Atlantic, etc.) highest — that's specifically what Mark is targeting. A paper with 500 citations but zero non-academic mention is field-internal; a paper with 50 citations and a NYT feature is the kind of reach Mark wants.

## Output structure (4-section deliverable)

### Section 1: Positive set (6-10 papers)

For each:
- **Citation** (full author/year/venue)
- **Multi-signal score breakdown** (per-signal score, weighted total)
- **Opening-line analysis** — what does the abstract / §1 first sentence do? What register, what image, what kind of hook?
- **Language-register profile**:
  - Emotion-specific vocabulary count (count of emotion-naming words: feel, want, hurt, longing, joy, etc., per 1000 words of text) — only relevant for emotion-AI papers
  - Jargon density (count of technical-noun-heavy sentences as % of total)
  - Anecdote count (number of distinct concrete-moment / case-study / vignette references)
  - First-person voice (does the author say "I" or "we"; how often)
- **Specific craft device** — name and exemplify ONE craft pattern the paper uses well (e.g., *"opens with a system transcript before any framing"*, *"leads with reader's likely objection"*, *"frames the contribution as a question rather than a claim"*).

### Section 2: Negative set (4-6 papers)

Papers with strong technical content but flat reception (low cross-disciplinary reach despite reasonable in-field citation). For each:
- **Citation**
- **What's strong** about it (so it doesn't read as dunking)
- **Specific register choice** that likely limited reach — diagnose with the same instruments as the positive set, but with a focus on *what the paper would have done differently to land more broadly*.

Be charitable. The negative set is for craft learning, not field critique.

### Section 3: Language-register hypothesis test (Mark's specific question)

Mark's hypothesis: *for emotion-AI papers specifically, the high-engagement ones use emotion-specific vocabulary; the low-engagement ones retreat to neutral academic register.*

Test by:
- Filtering both sets to only papers about emotional / affective / companion / relational AI.
- Comparing emotion-specific-vocabulary frequency between positive and negative sets.
- Reporting actual numbers (not just *"yes the hypothesis holds"*).
- Naming any unexpected pattern that surfaces (e.g., *"high-engagement emotion-AI papers ALSO use the words `attention` and `mechanism` heavily — these are not avoiding technical vocabulary, they're balancing it with emotion-specific vocabulary"*).

Confirm or reject the hypothesis with empirical evidence. If the hypothesis is partially true, name where.

### Section 4: Craft typology

The 4-7 distinct opening-paragraph and framing-device patterns observed across the positive set, named and exemplified. Becomes a reusable reference. Examples of pattern names ml-intern might surface:

- *"Open with system output / transcript"* (the paper leads with what the AI actually said before any analysis)
- *"Open with reader's likely objection"* (acknowledges the doubt before earning the read)
- *"Open with concrete deployment moment"* (specific time, specific event, specific data)
- *"Open with research question as plain English"*
- *"Open with the historical / cultural / philosophical reference"* (Game of Life, Conway, Turing, etc.)

Don't impose these — let the patterns surface from the actual papers, then name them.

## Constraints

- **No fabrication.** If a signal can't be obtained for a paper, report `unknown` rather than estimate. Multi-signal weighting handles missing data fine.
- **Quote sparingly.** Pull-quotes ≤30 words each, max 2 per paper. The deliverable is analysis, not anthology.
- **No URL hallucination.** Cite arXiv IDs and DOIs only when verified.
- **Paper-3-relevance flag.** When a positive-set paper is methodologically relevant to ANI's Paper 3 work (centrality gravity, supersession, conscience layer, emergence layer), flag it explicitly. ml-intern's prior surveys produced cross-references that shaped Paper 2's §6.10 — same possibility here.

## Success criteria

The deliverable is useful if Mark can read it once and walk away with:
1. 6-10 papers he hadn't read but should now read for craft reasons (not for content).
2. A clear answer on whether emotion-specific vocabulary correlates with cross-disciplinary reach in emotion-AI research.
3. 4-7 named craft patterns he can apply to his next paper draft / LinkedIn post / future conference talk.

If Mark reads it and can't say *"oh, here's what I should change about my abstract"*, the survey didn't land.

## Tracking

Add a row to `docs/spec/ANI-Phase-Tracker.md` Research Gap Watch table once the survey output lands, with the survey's headline finding and any new gap identified. Standard practice from prior ml-intern runs.
