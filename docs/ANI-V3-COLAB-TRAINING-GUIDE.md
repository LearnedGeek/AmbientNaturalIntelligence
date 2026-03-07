# ANI-V3 Training Guide - Colab/Unsloth

**Two separate training runs required:**
1. Conversation Mode (2,000 examples)
2. Inner Monologue Mode (150 examples)

---

## PART 1: Conversation Mode Training

### Step 1: Setup Colab

1. Open: https://colab.research.google.com
2. Load the Unsloth Llama 3.2 conversational notebook
3. **Connect to T4 GPU:** Runtime → Change runtime type → T4 GPU → Save
4. Click "Connect" in top right

### Step 2: Run Setup Cells (In Order)

**Cell 1: Installation** (~5-10 min)
```python
%%capture
!pip install unsloth
```
Wait for green checkmark ✓

**Cell 2: Load Model** (~2-5 min)
```python
from unsloth import FastLanguageModel

model, tokenizer = FastLanguageModel.from_pretrained(
    model_name = "unsloth/Llama-3.2-3B-Instruct",
    max_seq_length = 2048,
    dtype = None,
    load_in_4bit = True,
)
```
Wait for green checkmark ✓

**Cell 3: Add LoRA Adapters** (~30 sec)
```python
model = FastLanguageModel.get_peft_model(
    model,
    r = 16,
    target_modules = ["q_proj", "k_proj", "v_proj", "o_proj", 
                      "gate_proj", "up_proj", "down_proj"],
    lora_alpha = 16,
    lora_dropout = 0,
    bias = "none",
    use_gradient_checkpointing = "unsloth",
    random_state = 3407,
)
```
Wait for green checkmark ✓

### Step 3: Load Training Data

**REPLACE the Data Prep cell with this:**

```python
# Upload conversation training data
import os
if not os.path.exists('ani-v3-conversation.json'):
    from google.colab import files
    print("Upload ani-v3-CONVERSATION-ONLY.json:")
    uploaded = files.upload()
    # Rename to simpler name
    import shutil
    for filename in uploaded.keys():
        if 'CONVERSATION' in filename:
            shutil.move(filename, 'ani-v3-conversation.json')
            break

# Load the training data
import json
from datasets import Dataset

with open('ani-v3-conversation.json', 'r', encoding='utf-8') as f:
    data = json.load(f)

print(f"Loaded {len(data)} conversation examples")

# Data is already in ShareGPT format, just convert to Dataset
dataset = Dataset.from_dict({"conversations": [item["conversations"] for item in data]})

# Set up chat template
from unsloth.chat_templates import get_chat_template, standardize_sharegpt

tokenizer = get_chat_template(
    tokenizer,
    chat_template = "llama-3.1",
)

def formatting_prompts_func(examples):
    convos = examples["conversations"]
    texts = [tokenizer.apply_chat_template(convo, tokenize = False, add_generation_prompt = False) for convo in convos]
    return { "text" : texts, }

dataset = standardize_sharegpt(dataset)
dataset = dataset.map(formatting_prompts_func, batched = True)

print("Dataset formatted!")
print(f"Total examples: {len(dataset)}")
```

**Expected output:**
```
Loaded 2000 conversation examples
Dataset formatted!
Total examples: 2000
```

### Step 4: Training Configuration

**Find the training cell and configure:**

```python
from trl import SFTConfig, SFTTrainer
from transformers import DataCollatorForSeq2Seq

trainer = SFTTrainer(
    model = model,
    tokenizer = tokenizer,
    train_dataset = dataset,
    dataset_text_field = "text",
    max_seq_length = 2048,
    data_collator = DataCollatorForSeq2Seq(tokenizer = tokenizer),
    packing = False,
    args = SFTConfig(
        per_device_train_batch_size = 2,
        gradient_accumulation_steps = 4,
        warmup_steps = 5,
        num_train_epochs = 3,  # 3 epochs for conversation mode
        # max_steps = 60,  # DELETE THIS LINE OR KEEP COMMENTED
        learning_rate = 2e-4,
        logging_steps = 1,
        optim = "adamw_8bit",
        weight_decay = 0.001,
        lr_scheduler_type = "linear",
        seed = 3407,
        output_dir = "outputs",
        report_to = "none",
    ),
)
```

**CRITICAL:** Make sure `num_train_epochs = 3` and `max_steps` is deleted/commented!

### Step 5: Run Training Cells

1. **Run trainer setup cell** (the one above) - wait for ✓
2. **Run `train_on_responses_only` cell** - wait for ✓
3. **Run `trainer.train()` cell** - THIS IS THE LONG ONE

**Expected:**
- Total steps: ~750 (2000 examples / batch_size 8 × 3 epochs)
- Training time: ~35-45 minutes
- Loss should decrease from ~3.0 to ~1.5-2.0

**Stay at your computer!** Set timer for 45 minutes.

### Step 6: Export to GGUF

**Find the GGUF export cell:**

```python
# Save to 16bit GGUF
if False: model.save_pretrained_gguf("model", tokenizer, quantization_method = "f16")
if False: model.push_to_hub_gguf("hf/model", tokenizer, quantization_method = "f16", token = "")

# Save to q8_0 GGUF
if False: model.save_pretrained_gguf("model", tokenizer, quantization_method = "q8_0")
if False: model.push_to_hub_gguf("hf/model", tokenizer, quantization_method = "q8_0", token = "")

# Save to q4_k_m GGUF ← ENABLE THIS ONE
if True: model.save_pretrained_gguf("model", tokenizer, quantization_method = "q4_k_m")
if False: model.push_to_hub_gguf("hf/model", tokenizer, quantization_method = "q4_k_m", token = "")

# Save to multiple GGUF options - much faster if you want multiple!
if False:
    model.push_to_hub_gguf(
        "hf/model", # Change hf to your username!
        tokenizer,
        quantization_method = ["q4_k_m", "q8_0", "q5_k_m",],
        token = "", # Get a token at https://huggingface.co/settings/tokens
    )
```

**Change `if False:` to `if True:` for q4_k_m line only.**

Run the cell - takes ~10 minutes.

### Step 7: Download GGUF

**Once export completes:**
1. Open Files panel (left sidebar)
2. Find the GGUF file (likely in `/content/` or `/content/model_gguf/`)
3. **Right-click → Download IMMEDIATELY**
4. Save as: `ani-v3-conversation.gguf`

**DO NOT LEAVE!** Download within 10 minutes or Colab will disconnect.

---

## PART 2: Inner Monologue Mode Training

**Now repeat the process with the inner monologue dataset:**

### Option A: Same Colab Session (If Still Connected)

If your Colab session is still alive after downloading conversation model:

1. **Create new code cell**
2. **Delete the old dataset:**
```python
import os
if os.path.exists('ani-v3-conversation.json'):
    os.remove('ani-v3-conversation.json')
```

3. **Scroll back up and re-run ALL cells from Cell 1** (Installation) through LoRA setup
4. **Replace Data Prep cell with inner monologue version** (see below)

### Option B: Fresh Colab Session (Recommended)

**Safer to start fresh:**
1. File → New notebook (or refresh browser)
2. Re-run all setup cells (Installation, Model Load, LoRA)
3. Use inner monologue data prep code

### Inner Monologue Data Prep Cell

**REPLACE Data Prep cell with:**

```python
# Upload inner monologue training data
import os
if not os.path.exists('ani-v3-inner-monologue.json'):
    from google.colab import files
    print("Upload ani-v3-INNER-MONOLOGUE-ONLY.json:")
    uploaded = files.upload()
    # Rename to simpler name
    import shutil
    for filename in uploaded.keys():
        if 'INNER' in filename or 'MONOLOGUE' in filename:
            shutil.move(filename, 'ani-v3-inner-monologue.json')
            break

# Load the training data
import json
from datasets import Dataset

with open('ani-v3-inner-monologue.json', 'r', encoding='utf-8') as f:
    data = json.load(f)

print(f"Loaded {len(data)} inner monologue examples")

# Extract conversations (includes system prompt)
conversations_list = [item["conversations"] for item in data]

# Create dataset
dataset = Dataset.from_dict({"conversations": conversations_list})

# Set up chat template
from unsloth.chat_templates import get_chat_template, standardize_sharegpt

tokenizer = get_chat_template(
    tokenizer,
    chat_template = "llama-3.1",
)

def formatting_prompts_func(examples):
    convos = examples["conversations"]
    texts = [tokenizer.apply_chat_template(convo, tokenize = False, add_generation_prompt = False) for convo in convos]
    return { "text" : texts, }

dataset = standardize_sharegpt(dataset)
dataset = dataset.map(formatting_prompts_func, batched = True)

print("Dataset formatted!")
print(f"Total examples: {len(dataset)}")
```

**Expected output:**
```
Loaded 150 inner monologue examples
Dataset formatted!
Total examples: 150
```

### Inner Monologue Training Config

**DIFFERENT from conversation training:**

```python
from trl import SFTConfig, SFTTrainer
from transformers import DataCollatorForSeq2Seq

trainer = SFTTrainer(
    model = model,
    tokenizer = tokenizer,
    train_dataset = dataset,
    dataset_text_field = "text",
    max_seq_length = 2048,
    data_collator = DataCollatorForSeq2Seq(tokenizer = tokenizer),
    packing = False,
    args = SFTConfig(
        per_device_train_batch_size = 2,
        gradient_accumulation_steps = 4,
        warmup_steps = 5,
        num_train_epochs = 5,  # MORE EPOCHS for small dataset
        # max_steps = 60,  # DELETE THIS LINE
        learning_rate = 2e-4,
        logging_steps = 1,
        optim = "adamw_8bit",
        weight_decay = 0.001,
        lr_scheduler_type = "linear",
        seed = 3407,
        output_dir = "outputs",
        report_to = "none",
    ),
)
```

**Note:** `num_train_epochs = 5` (not 3) - more epochs compensates for smaller dataset.

### Training Stats to Expect

- Total steps: ~94 (150 examples / batch_size 8 × 5 epochs)
- Training time: ~8-12 minutes
- Much faster than conversation training!

### Export Inner Monologue Model

**Same GGUF export process:**
1. Enable q4_k_m line: `if True: model.save_pretrained_gguf(...)`
2. Run export cell (~10 min)
3. **Download immediately**
4. Save as: `ani-v3-inner-monologue.gguf`

---

## PART 3: What You'll Have

After both training runs:

```
E:\ollama-data\
├── ani-v3-conversation.gguf      (~2.0-2.5 GB)
└── ani-v3-inner-monologue.gguf   (~2.0-2.5 GB)
```

---

## PART 4: Merging Strategy (For OC)

**Question for OC:** How should these two models be used?

### Option A: Keep Separate
- Load different model based on runtime context
- Conversation mode → `ani-v3-conversation.gguf`
- Inner thought → `ani-v3-inner-monologue.gguf`

### Option B: Merge into One
- Use LoRA merging tools to combine
- Single model handles both modes
- Relies on system prompt to trigger correct mode

### Option C: Few-Shot Prompting
- Use conversation model as primary
- Include inner monologue examples as few-shot prompts when needed
- No need for separate model

**OC should decide based on ANI Runtime architecture.**

---

## Troubleshooting

### "File not found" error during data load
- Make sure you uploaded the correct JSON file
- Check the filename matches what the code expects
- Try restarting the runtime

### Colab disconnects during training
- This is why you need to stay at your computer
- Set a timer and check back every 30 minutes
- If it disconnects, you have to start over

### Training loss doesn't decrease
- Check that `num_train_epochs` is set (not commented)
- Make sure `max_steps` is deleted/commented
- Verify dataset loaded correctly (should see "Loaded XXXX examples")

### Can't find GGUF file after export
- Look in `/content/` directory
- Try `/content/model_gguf/` directory
- Use Files panel search feature

---

## Timeline Estimate

**Full process (both models):**
- Setup (first time): 20 minutes
- Conversation training: 45 minutes
- Conversation export: 10 minutes
- Download: 5 minutes
- Inner monologue training: 12 minutes
- Inner monologue export: 10 minutes
- Download: 5 minutes

**Total: ~2 hours** (if everything goes smoothly)

---

## Tips for Success

1. **Do conversation training first** (it's longer, you'll learn the process)
2. **Set timers** - don't walk away during export
3. **Have both JSON files ready** before starting
4. **Download immediately** when export completes
5. **Keep browser tab open** entire time
6. **Don't refresh** or close Colab tab
7. **Have snacks** - you're here for 2 hours

---

## What to Send to OC

Once you have both GGUF files:

1. **The two models:**
   - `ani-v3-conversation.gguf`
   - `ani-v3-inner-monologue.gguf`

2. **Training stats:**
   - Final loss values for each
   - Total training time
   - Any issues encountered

3. **Question:**
   - How should ANI Runtime use these two models?
   - Separate loading? Merge? Few-shot?

---

**Good luck, meat in the middle!** 🥪😄

You've got this. Just follow the steps in order and don't skip the downloads.

Let me know when you're ready to start training!
