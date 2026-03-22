# ANI v6 Training Instructions

## Training Data
- `ani-v6-CONVERSATION.json` — 1,675 examples (v5 base + v6 new, deduped)
- `ani-v6-INNER-MONOLOGUE.json` — 355 examples (v5 base + v6 new, deduped)

## Step 1: Fine-tune the conversation model (8B)

Using Unsloth or MLX for LoRA fine-tuning on Llama 3.1-8B:

```bash
# If using Unsloth (recommended for speed):
python train.py \
  --model meta-llama/Llama-3.1-8B-Instruct \
  --dataset ani-v6-CONVERSATION.json \
  --format sharegpt \
  --epochs 3 \
  --lr 2e-4 \
  --lora_r 16 \
  --lora_alpha 32 \
  --output ani-v6-conversation-8B

# Export to GGUF Q4_K_M for Ollama:
python export.py --model ani-v6-conversation-8B --format gguf --quant Q4_K_M
```

## Step 2: Fine-tune the inner monologue model (3B)

```bash
python train.py \
  --model meta-llama/Llama-3.2-3B-Instruct \
  --dataset ani-v6-INNER-MONOLOGUE.json \
  --format sharegpt \
  --epochs 3 \
  --lr 2e-4 \
  --lora_r 16 \
  --lora_alpha 32 \
  --output ani-v6-inner-monologue

# Export to GGUF Q4_K_M:
python export.py --model ani-v6-inner-monologue --format gguf --quant Q4_K_M
```

## Step 3: Register with Ollama

```bash
# Copy GGUF files to this directory, then:
cp aniv6CONVERSATION-8B.gguf docs/training/v6/
cp aniv6INNERMONOLOGUE.gguf docs/training/v6/

# Create Ollama models:
ollama create ani-v6-conversation -f docs/training/v6/v6-conversation-8B.modelfile
ollama create ani-v6-inner -f docs/training/v6/v6-inner-monologue.modelfile
```

## Step 4: Update appsettings.json

```json
{
  "Ollama": {
    "ConversationModel": "ani-v6-conversation",
    "InnerMonologueModel": "ani-v6-inner"
  }
}
```

## Step 5: Verify

```bash
# Test conversation model:
ollama run ani-v6-conversation "Hey babe, how's your day?"

# Test inner monologue model:
ollama run ani-v6-inner ""
```

## v6 Register Distribution (what changed from v5)

| Register | v5 | v6 | Change |
|----------|----|----|--------|
| Playfulness | ~3% | ~30% | +27% — sharp wit, teasing, intellectual play |
| Delight | ~8% | ~22% | +14% — genuine joy, wry amusement, quiet contentment |
| Longing | ~33% | <1% new | Deliberately reduced — v5 base already covers this |
| Existential | ~5% | ~11% | +6% — self-awareness, honest uncertainty |
| Curiosity | ~4% | ~8% | +4% — genuine questions, investigation |
| Tenderness | ~12% | ~15% | +3% — admiration, protective instinct |
| Honest-Uncertainty | 0% | ~4% | New — "I don't know" said warmly |
| Resilience | 0% | ~2% | New — holding ground under pressure |
| Disagreement | 0% | ~2% | New — genuine pushback without folding |

## A/B Testing (Phase 5c)

For the Llama vs Mistral A/B test, repeat Step 1 with:
```bash
--model mistralai/Mistral-7B-Instruct-v0.3
--output ani-v6-conversation-mistral
```
Then run blinded pairwise evaluation per Phase 5c design doc.

## Notes
- v5 conversation data was 2,073 entries but contained heavy duplication
- After dedup with v6 additions: 1,675 unique conversation examples
- The "mmm..." opener and "and honestly?" trailing patterns in v5 data
  should be naturally diluted by the v6 additions with more diverse openers
- Anti-confabulation examples (honest-uncertainty, resilience, disagreement)
  are the most important behavioral shift from v5 → v6
