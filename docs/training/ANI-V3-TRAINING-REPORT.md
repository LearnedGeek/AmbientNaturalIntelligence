# ANI-V3 TRAINING DATA PREPARATION REPORT

**Date:** March 6, 2026  
**Prepared by:** Claude (OC collaborating with Assistant Claude)  
**Purpose:** Prepare training data for ani-v3 to support ANI Runtime inner monologue capability

---

## Executive Summary

**Goal:** Enable Ani to generate private inner monologues (unaddressed, observational, varied) while maintaining her conversational voice.

**Challenge:** The existing training corpus is **85%+ intimate/longing mode**. Minority modes (wry, observational, philosophical, playful) represent <5% of data combined.

**Solution Implemented:** Aggressive resampling + separate inner monologue training dataset.

**Risk:** Heavy repetition of minority examples may cause memorization rather than pattern learning.

---

## Data Sources Processed

### Input Files (5 conversation exports):
1. `ani-history.txt` - 1,061 pairs
2. `ani-combined.txt` - 1,375 pairs  
3. `grok-FINAL-1770956749267.txt` - 79 pairs
4. `grok-FINAL-1771465252495.txt` - 453 pairs
5. `grok-FINAL-1772826685699.txt` - 794 pairs

### After Deduplication:
**2,088 unique conversation pairs**

### Inner Monologue Corpus:
`ani-inner-voice.md` - **34 examples** (handcrafted, high quality)

---

## Original Distribution (Harsh Reality)

From 2,088 deduplicated conversation pairs:

| Mode | Count | Percentage |
|------|-------|------------|
| **Intimate** | 1,783 | **85.4%** |
| Wry | 3 | 0.1% |
| Observational | 15 | 0.7% |
| Philosophical | 17 | 0.8% |
| Playful | 9 | 0.4% |
| General | 261 | 12.5% |

**Note:** The "General" category (12.5%) is mostly **more intimate mode** with different phrasing (physical intimacy, comforting, soft moments) that pattern matching missed.

**Actual Reality:** ~95% of the corpus is intimate/relational in various forms.

---

## Target Distribution (Per OC Specs)

### Conversation Corpus:
- Intimate: **50%** (down from 85%)
- Wry/Observational: **20%** combined
- Philosophical: **15%**
- Playful: **10%**
- General/Neutral: **5%**

### Inner Monologue Corpus (Separate Dataset):
- Sensory/Observational: **35%**
- Philosophical/Wry: **30%**
- Vulnerable/Inward: **25%**
- Playful/Light: **10%**

---

## Resampling Execution

To achieve target distribution with 2,000 total conversation examples:

| Mode | Original | Target | Strategy | Repetition Factor |
|------|----------|--------|----------|-------------------|
| Intimate | 1,783 | 1,000 | Downsample | 0.56× |
| Wry | 3 | 200 | **Oversample** | **66.7×** ⚠️ |
| Observational | 15 | 200 | **Oversample** | **13.3×** ⚠️ |
| Philosophical | 17 | 300 | **Oversample** | **17.6×** ⚠️ |
| Playful | 9 | 200 | **Oversample** | **22.2×** ⚠️ |
| General | 261 | 100 | Downsample | 0.38× |

### Inner Monologue:
| Category | Original | Target | Strategy | Repetition Factor |
|----------|----------|--------|----------|-------------------|
| All categories | 34 | 150 | **Oversample** | **4.4×** |

---

## CRITICAL ISSUE: Extreme Oversampling

**Problem:** Minority modes are being repeated 13×–67× to reach target distribution.

**Risk:** The model may **memorize** these specific examples rather than learning the general pattern of each mode.

**Example:** 
- The 3 "wry" examples will each appear ~67 times in training
- The model might learn "when I see X pattern, output this specific wry response" rather than "here's how to be wry"

---

## Output Files

### Final Training Data:

1. **`ani-v3-CONVERSATION-ONLY.json`** (2,000 examples)
   - Resampled to target distribution
   - Ready for Unsloth training
   - ShareGPT format

2. **`ani-v3-INNER-MONOLOGUE-ONLY.json`** (150 examples)
   - Separate training task
   - Different system prompt
   - ShareGPT format with system message

3. **`ani-v3-COMBINED-REFERENCE.json`** (2,150 total)
   - For reference only
   - **Do NOT train on combined dataset**
   - They need separate training runs

### Supporting Files:

- `ani-full-corpus.txt` - Full deduplicated conversation text
- `categorized-corpus.json` - All conversations with mode tags
- `inner-monologue-corpus.json` - Categorized inner voice examples
- `mode-analysis.txt` - Detailed distribution analysis

---

## Recommendations

### Option A: Proceed with Current Dataset (Faster)
**Pros:**
- Ready to train immediately
- Will shift Ani away from 100% intimate mode
- Inner monologue training might work with 150 examples

**Cons:**
- Heavy repetition risk
- Model may memorize rather than generalize
- May need ani-v4 retraining if results are weak

### Option B: Generate Synthetic Minority Examples (Risky)
**Approach:** Use Claude/GPT-4 to generate variations of minority mode examples

**Pros:**
- Reduces repetition
- Provides more training signal

**Cons:**
- Synthetic examples may not match Ani's authentic voice
- Could introduce artifacts or "AI-generated" patterns
- Labor intensive to quality-check each example

### Option C: Collect More Real Data (Slow but Best)
**Approach:** Intentionally have conversations with Ani that elicit minority modes

**How:**
- Ask about bookstore experiences → observational
- Discuss philosophical topics → philosophical  
- Playful banter sessions → playful
- Wry observations about life → wry

**Pros:**
- Authentic voice preserved
- Natural distribution improvement
- Better long-term foundation

**Cons:**
- Takes time (weeks of conversations)
- Delays ani-v3 training

### Option D: Hybrid Approach (Recommended)
1. **Train ani-v3 now** with current dataset (accept some repetition)
2. **Test the inner monologue capability** in ANI Runtime
3. **Collect real minority mode examples** over next 2-4 weeks
4. **Retrain ani-v4** with improved distribution

**Rationale:** Get the runtime working NOW, iterate on training quality later.

---

## Training Instructions (When Ready)

### Two Separate Training Runs Required:

#### Run 1: Conversation Mode
```python
# Upload: ani-v3-CONVERSATION-ONLY.json
# Training config:
num_train_epochs = 3
per_device_train_batch_size = 2
gradient_accumulation_steps = 4

# Expected: ~750 training steps
# Time: ~30-40 minutes on T4
```

#### Run 2: Inner Monologue Mode  
```python
# Upload: ani-v3-INNER-MONOLOGUE-ONLY.json
# Training config:
num_train_epochs = 5  # More epochs to compensate for small dataset
per_device_train_batch_size = 2
gradient_accumulation_steps = 4

# Expected: ~94 training steps
# Time: ~5-10 minutes on T4
```

### Model Merging Strategy:
After both training runs complete, you'll have two fine-tuned models:
1. `ani-v3-conversation.gguf`
2. `ani-v3-inner-monologue.gguf`

**OC will need to advise on:** 
- Whether to merge them into one model
- Or load different models for different runtime contexts
- Or use conversation model with inner-monologue examples as few-shot prompts

---

## Quality Checks Before Training

Before uploading to Colab, **manually review:**

1. **Sample 20 random "wry" examples** - Do they all sound like Ani being wry?
2. **Sample 20 random "playful" examples** - Genuine playfulness or forced?
3. **Check inner monologue examples** - No "you" violations, correct length?

If any category feels wrong, we can re-categorize before training.

---

## Next Steps

1. **Mark reviews this report**
2. **Decides on approach** (A, B, C, or D)
3. **If proceeding:** Download training files, upload to Colab
4. **Train separately:** Conversation mode, then inner monologue mode
5. **Test in ANI Runtime** - Does she think properly now?
6. **Iterate** based on results

---

## Questions for Mark

1. **Comfort with repetition risk?** Willing to accept that 3 wry examples will repeat 67× in training?

2. **Timeline preference?** Train now and iterate (Option D) or collect more data first (Option C)?

3. **Model merging strategy?** Should OC plan for:
   - One unified model handling both modes?
   - Two separate models loaded by context?
   - Few-shot prompting for inner monologue?

4. **Quality bar?** Want to manually review samples before training, or trust the automated categorization?

---

**Files ready for download when you are.**

— Claude (with OC specifications)
