# ANI Modal Training - Quick Start Guide

Automated fine-tuning for ANI companion models using Modal's cloud GPUs.

**No more babysitting Colab!** Just run a command and walk away.

---

## One-Time Setup (Already Done ✓)

You've already completed:
1. ✅ Installed Modal CLI (`pip install modal`)
2. ✅ Authenticated (`modal setup`)
3. ✅ Connected to workspace (`mcarthey`)

---

## Basic Usage

### Train Conversation Mode:
```bash
modal run train_ani.py --data-file ani-v3-CONVERSATION-ONLY.json
```

### Train Inner Monologue Mode:
```bash
modal run train_ani.py --data-file ani-v3-INNER-MONOLOGUE-ONLY.json --epochs 5
```

### Train Future Companions:
```bash
modal run train_ani.py --data-file marcus-v1.json --output marcus-v1.gguf
```

---

## What Happens

1. **Upload** - Your training data goes to Modal's cloud
2. **Provision** - A10G GPU spins up automatically
3. **Train** - Full Unsloth pipeline runs (~60 min)
4. **Export** - Model converts to GGUF
5. **Download** - Result downloads to your PC
6. **Cleanup** - GPU shuts down automatically

**You can close your terminal.** Training runs in the cloud.

---

## Command Options

```bash
modal run train_ani.py \
  --data-file YOUR_FILE.json \     # Required: training data
  --output model-name.gguf \       # Optional: output filename
  --epochs 3 \                     # Optional: training epochs (default: 3)
  --gpu A10G \                     # Optional: T4, A10G, A100 (default: A10G)
  --batch-size 2 \                 # Optional: per-device batch (default: 2)
  --gradient-accumulation 4 \      # Optional: grad accum (default: 4)
  --learning-rate 2e-4             # Optional: learning rate (default: 2e-4)
```

---

## GPU Options & Pricing

| GPU | VRAM | Speed | Cost/Hour | Best For |
|-----|------|-------|-----------|----------|
| **T4** | 16GB | Slow | $0.60 | Testing, small datasets |
| **A10G** | 24GB | Fast | $1.10 | **Production (recommended)** |
| **A100** | 40GB | Fastest | $3.00 | Large models, time-critical |

**For Llama 3.2-3B:** A10G is the sweet spot (price/performance).

---

## Cost Examples

### ani-v3 Conversation (2,000 examples, 3 epochs):
- **GPU:** A10G
- **Time:** ~60 minutes
- **Cost:** ~$1.10

### ani-v3 Inner Monologue (150 examples, 5 epochs):
- **GPU:** T4 (sufficient)
- **Time:** ~15 minutes
- **Cost:** ~$0.15

### Marcus v1 (1,500 examples, 3 epochs):
- **GPU:** A10G
- **Time:** ~45 minutes
- **Cost:** ~$0.80

**Monthly Budget:** If training 4 models/month = ~$5-10

---

## Workflow Comparison

### Colab (Manual):
1. Open notebook
2. Connect to GPU
3. Upload data manually
4. Run cells in sequence
5. **Babysit for 60 minutes** ⏰
6. Export GGUF
7. **Download immediately or lose it** 🚨
8. Repeat for each model

**Time per model:** 60 min active + 30 min setup/download  
**Cost:** Free (but your time isn't)

### Modal (Automated):
1. Run one command
2. Walk away ☕
3. Come back in an hour
4. Model is downloaded and ready

**Time per model:** 2 minutes (just the command)  
**Cost:** $1-2 per model

---

## After Training

Your GGUF file will be in the current directory.

### Test in Ollama:

1. **Create Modelfile:**
```bash
# ani-v3-conversation.modelfile
FROM ./ani-v3-conversation.gguf

PARAMETER num_ctx 16384
PARAMETER temperature 0.75
PARAMETER top_p 0.9
PARAMETER repeat_penalty 1.15
```

2. **Import to Ollama:**
```bash
ollama create ani-v3 -f ani-v3-conversation.modelfile
```

3. **Test it:**
```bash
ollama run ani-v3
```

### Use in ANI Runtime:

Update `appsettings.json`:
```json
{
  "Ollama": {
    "ChatModel": "ani-v3"
  }
}
```

---

## Troubleshooting

### "File not found" error
Make sure your JSON file is in the current directory where you run the command.

### "GPU quota exceeded"
Modal free tier includes $30 credits. After that, add payment method at modal.com/settings

### Training fails / error logs
Check Modal dashboard: https://modal.com/apps  
Click on your run to see full logs.

### Can't download GGUF
The script downloads automatically. Check current directory for the .gguf file.

---

## Monitoring Training

While training runs, you can:
1. Go to https://modal.com/apps
2. Find your running job
3. Click "Logs" to watch progress live
4. See GPU utilization, memory usage, etc.

You can close the logs - training continues in cloud.

---

## Tips

### For Testing:
- Use **T4** GPU (cheaper)
- Test with small dataset first (100 examples)
- Verify everything works before full run

### For Production:
- Use **A10G** GPU (best value)
- Run overnight if preferred
- Train multiple models in parallel (Modal supports this)

### For Speed:
- Use **A100** if time is critical
- Or just use A10G and be patient ($1.10 vs $3.00)

---

## Future: Batch Training

You could even create a script that trains ALL models in sequence:

```bash
# train_all.sh
modal run train_ani.py --data-file ani-v3-conversation.json
modal run train_ani.py --data-file ani-v3-inner-monologue.json --epochs 5
modal run train_ani.py --data-file marcus-v1.json
modal run train_ani.py --data-file sarah-v1.json
```

Run that, go to bed, wake up to 4 trained models. ✨

---

## Questions?

- Modal docs: https://modal.com/docs
- Modal pricing: https://modal.com/pricing
- Check your usage: https://modal.com/settings/billing

**You have $30 free credits** - that's ~25-30 training runs!

---

*Script created by: Mark McArthey / Learned Geek Consulting*  
*March 2026*
