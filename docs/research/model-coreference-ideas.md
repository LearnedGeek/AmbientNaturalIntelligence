# Pronoun Attribution & Coreference Resolution in LLMs

**Research Notes — ANI Project / Universal Gate Architecture**
*May 2, 2026*

---

## Background & Problem Statement

The question driving this conversation: how do large language models handle pronoun attribution in mixed sentences, and why does it fail more often than expected?

Example of the problem:

> *"I told her to go to the store to get something for him."*

In English grammar, pronoun binding rules are well-defined — pronouns bind to their nearest appropriate antecedent with matching gender/number. To a human, the bindings above are unambiguous in context. To an LLM, they are not reliably so.

---

## Why LLMs Struggle with Pronoun Attribution

LLMs do not apply explicit grammar rules. They learn pronoun binding from statistical patterns in training data. This creates several failure modes:

**Ambiguity in training data.** Real English is genuinely ambiguous in many pronoun contexts. The model has seen all possible binding patterns and has no ground truth to anchor to.

**No syntactic parsing.** LLMs process tokens via attention mechanisms, not by building a parse tree. Pronoun binding is learned as a statistical heuristic ("the closer antecedent usually wins"), not as a rule.

**Context window drift.** In multi-turn conversations with multiple entities mentioned across many turns, attention weights — not rules — determine which prior entity a pronoun resolves to. If a male name was mentioned five turns ago and a female name two turns ago, the model makes a weighted guess.

**Mixed-sentence failure.** In sentences with multiple pronouns of different genders, the model sometimes inverts bindings or assigns them to the wrong entity entirely.

---

## The Deeper Problem: Entity Tracking

Source attribution — tracking who said what — is one layer of the problem. But there is a deeper layer: **entity tracking across the full conversation**.

What the model needs (but doesn't maintain) is something like an explicit entity roster:

- Who are the active entities in the conversation?
- What pronoun(s) does each use or respond to?
- What gender/number agreement applies to each?
- When a pronoun appears, which entity does it bind to?

LLMs learn statistical approximations of this tracking. They do not maintain an explicit, updateable state structure. The result is inconsistent binding, especially as conversation length grows.

**Coreference resolution** is the formal NLP subproblem name for this task: grouping all expressions in a text that refer to the same real-world entity.

---

## Architectural Approach: Universal Gate

The ANI project's approach is to address this as a **universal gate** — a post-generation processing layer that operates independently of any specific pipeline. The gate:

1. Receives model output
2. Runs coreference resolution on the output
3. Flags low-confidence or misattributed pronoun bindings
4. Can trigger re-generation or surface the binding for review

This is analogous to other gates already deployed in the architecture (e.g., emotional detection using a separate lightweight model). A small, purpose-built 1B-class coreference model running via LM-Kit is a viable path — parallel to the existing emotional detection model.

---

## Key Research Papers

### Foundational Work

**Ng & Cardie (2002)** — *"Improving Machine Learning Approaches to Coreference Resolution"*
Established the foundational problem formulation for ML-based coreference resolution.

**Lee et al. (2017)** — *"End-to-end Neural Coreference Resolution"* (ACL)
Introduced the span-based neural approach that became the standard architecture. This underpins most modern tools including AllenNLP's coreference model.

**Joshi et al. (2019)** — *"BERT for Coreference Resolution"* (EMNLP)
Demonstrated BERT-scale transformers applied to coreference — seminal for the neural era.

### Lightweight / Efficient Approaches

**Kirstain & Ram et al. (2021)** — *"Coreference Resolution without Span Representations"* (arXiv: 2101.00434)
Introduced a lightweight end-to-end model that removes dependency on span representations, handcrafted features, and heuristics. Competitive accuracy, significantly simpler. Direct precursor to modern fast coreference models.

**Maverick — Martinelli, Barba & Navigli (ACL 2024)** — *"Maverick: Efficient and Accurate Coreference Resolution Defying Recent Trends"*
State-of-the-art as of 2024. Outperforms much larger models with significantly fewer parameters and faster inference. Available on SapienzaNLP's Hugging Face hub.
- Paper: https://aclanthology.org/2024.acl-long.722
- GitHub: https://github.com/SapienzaNLP/maverick-coref

**F-Coref — biu-nlp (2022)**
Optimized for speed: processes 2,800 OntoNotes documents in 25 seconds on a V100 GPU (vs. 6 minutes for LingMess, 12 minutes for AllenNLP). Modest accuracy tradeoff.
- Hugging Face: `biu-nlp/f-coref`

---

## Candidate Models & Tools

### 1. spaCy Coreference Component (Recommended Starting Point)

- **Status:** Experimental, built into spaCy v3.8+
- **Architecture:** End-to-end neural, end-to-end trainable
- **Integration:** Native spaCy pipeline — add as a pipe component
- **Output:** `Doc.spans` as SpanGroups with cluster IDs
- **Advantage:** Minimal overhead if spaCy is already present; pipeline-composable

```python
import spacy

nlp = spacy.load("en_core_web_sm")
nlp.add_pipe("coref")
doc = nlp("Mark told Sarah to call him.")
# doc.spans["coref_clusters_0"] → cluster of coreferent spans
```

Reference: https://explosion.ai/blog/coref

---

### 2. spacy_coref (ONNX Runtime, Cross-lingual)

- **Status:** Active, maintained
- **Architecture:** MiniLM distilled from XLM-R Large, ONNX runtime inference
- **Advantage:** Lightweight, cross-lingual, fast inference via ONNX
- **Model:** `talmago/allennlp-coref-onnx-mMiniLMv2-L12-H384-distilled-from-XLMR-Large`
- GitHub: https://github.com/talmago/spacy_coref

```python
from spacy_coref import CoreferenceResolver, decode_clusters

resolver = CoreferenceResolver.from_pretrained(
    "talmago/allennlp-coref-onnx-mMiniLMv2-L12-H384-distilled-from-XLMR-Large"
)
sentences = [
    ["Mark", "told", "Sarah", "to", "call", "him", "."]
]
pred = resolver(sentences)
print(decode_clusters(sentences, pred["clusters"][0]))
```

---

### 3. Maverick (SapienzaNLP, ACL 2024)

- **Status:** Current state of the art (as of ACL 2024)
- **Architecture:** Efficient transformer-based; outperforms larger models
- **Advantage:** Best accuracy-to-size ratio available; actively maintained
- **Hugging Face hub:** SapienzaNLP organization
- **GitHub:** https://github.com/SapienzaNLP/maverick-coref

Best choice if accuracy is the priority and inference speed is secondary.

---

### 4. F-Coref (biu-nlp)

- **Status:** Stable
- **Architecture:** Optimized span-based; eliminates redundant computation
- **Advantage:** Fastest available; ~14x faster than AllenNLP with modest accuracy loss
- **Hugging Face:** `biu-nlp/f-coref`

Best choice if throughput is the priority (e.g., bulk post-processing of conversation logs).

---

### 5. AllenNLP Coreference (SpanBERT-based)

- **Status:** Repository archived (Dec 2022) — production use not recommended
- **Architecture:** SpanBERT-large; span-based Lee et al. 2017/2018 implementation
- **Note:** Still works, but Maverick and F-Coref supersede it in both speed and accuracy

---

## Design Notes for Universal Gate Implementation

**Input:** Entity roster (names + pronouns from conversation context) + candidate output text

**Output:** Per-pronoun binding with confidence score → (pronoun token, resolved entity, confidence)

**Gate behavior:**
- High confidence: pass through
- Low confidence or attribution conflict: flag for re-generation or surface for review

**Model size target:** 1B parameters or below, running via LM-Kit (same pattern as emotional detection model)

**Recommended evaluation benchmark:** OntoNotes 5.0 English — the standard benchmark for all models listed above, enabling direct accuracy comparison

**Fine-tuning opportunity:** Coreference models trained on OntoNotes (news/broadcast domain) may not generalize well to conversational text. Fine-tuning on conversation-domain data (e.g., MultiWOZ, PersonaChat, or ANI conversation logs) could significantly improve performance on the specific failure cases observed in deployment.

---

## Summary

| Model | Speed | Accuracy | Size | Status |
|---|---|---|---|---|
| spaCy coref | Fast | Good | Small | Experimental |
| spacy_coref (ONNX) | Very fast | Good | Small | Active |
| Maverick | Moderate | SOTA | Moderate | Active (ACL 2024) |
| F-Coref | Fastest | Good | Small | Stable |
| AllenNLP | Slow | Good | Large | Archived |

For a universal gate architecture prioritizing pipeline independence and low overhead: **spacy_coref (ONNX)** or **F-Coref** are the pragmatic starting points. For maximum accuracy with moderate compute: **Maverick**.