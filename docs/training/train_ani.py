"""
train_ani.py - Modal-based automated fine-tuning for ANI companions

Usage:
    modal run train_ani.py --data-file v7/ani-v7-CONVERSATION.json --base-model 8B
    modal run train_ani.py --data-file v7/ani-v7-INNER-MONOLOGUE.json --base-model 3B
    modal run train_ani.py --data-file v6/ani-v6-CONVERSATION.json --base-model mistral --output v6/aniv6CONVERSATION-mistral.gguf
    modal run train_ani.py --data-file v6/ani-v6-CONVERSATION.json --base-model unsloth/Phi-3.5-mini-instruct

Base models (shortcuts):
    3B       = unsloth/Llama-3.2-3B-Instruct  (default, ~2GB GGUF, fast CPU inference)
    8B       = unsloth/Llama-3.1-8B-Instruct  (~4.5GB GGUF, better instruction following)
    mistral  = unsloth/Mistral-7B-Instruct-v0.3  (~4.5GB GGUF, less safety-constrained)
    qwen     = unsloth/Qwen2.5-7B-Instruct  (~4.5GB GGUF, strong multilingual)
    gemma    = unsloth/gemma-2-9b-it  (~5GB GGUF, Google's best small model)

    Or pass any full HuggingFace model ID (e.g., unsloth/Phi-3.5-mini-instruct)

Modal GPU options (https://modal.com/blog/gpu-types):
    GPU          VRAM    Bandwidth   $/hr    Best for
    -------      ------  ---------   -----   --------
    T4           16 GB   0.3 TB/s    $0.59   Prototyping, small model inference
    L4           24 GB   0.3 TB/s    $0.80   Cost-efficient small/medium inference
    A10          24 GB   0.6 TB/s    $1.10   LoRA fine-tuning 3B-8B, inference 7B
    A100 (40GB)  40 GB   2.0 TB/s    $2.78   LoRA fine-tuning 8B+ (faster)
    A100 (80GB)  80 GB   2.0 TB/s    $3.40   Large model training 7B-70B
    H100         80 GB   3.4 TB/s    $4.56   70B+ training, fastest available

    Recommendation for ANI:
    - Inner monologue (3B, ~440 examples): A10 is fine, ~10 min, ~$0.18
    - Conversation (8B, ~2200 examples): A100 recommended, ~25 min, ~$0.79
    - Note: "A10G" also works in Modal (alias for A10). A100 is ~2x faster
      for 8B training and similar total cost since it finishes sooner.

    Actual v7 costs (April 2026):
    - ani-v7-inner (3B, 441 examples, 3 epochs, A10G): 9.8 min, $0.18
    - ani-v7-conversation (8B, 2240 examples, 3 epochs, A100): 43.3 min, $0.79
    - Total: $0.97 for both models

This script handles the complete training pipeline:
1. Upload training data to Modal
2. Load base model (3B or 8B)
3. Apply LoRA adapters
4. Fine-tune on your data
5. Export to GGUF format
6. Download result to your local machine

No babysitting required - just run and walk away!

Author: Mark McArthey / Learned Geek Consulting
Created: March 2026
"""

import modal
from pathlib import Path
import json

# Define the container image with all dependencies
image = (
    modal.Image.debian_slim(python_version="3.11")
    .apt_install(
        "git",
        "curl",
        "cmake",
        "libssl-dev",
        "libcurl4-openssl-dev",
        "build-essential",
    )
    .pip_install(
        "torch",
        "torchvision",
        "transformers",
        "datasets",
        "trl",
        "peft",
        "accelerate",
        "bitsandbytes",
        # Install Unsloth from GitHub
        "unsloth[colab-new] @ git+https://github.com/unslothai/unsloth.git",
    )
)

# Create the Modal app
app = modal.App("ani-training", image=image)

@app.function(
    gpu="A100",  # Change this to "A10G" or "A100" for different GPUs
    timeout=7200,  # 2 hours max
)
def train_model(
    training_data: str,  # JSON string of training data
    model_name: str = "ani-v3",
    num_epochs: int = 3,
    batch_size: int = 2,
    gradient_accumulation: int = 4,
    learning_rate: float = 2e-4,
    base_model: str = "3B",
):
    """
    Fine-tune a Llama model on Modal's GPU infrastructure

    Args:
        training_data: JSON string containing training examples
        model_name: Name for the output model
        num_epochs: Number of training epochs (default: 3)
        batch_size: Per-device batch size (default: 2)
        gradient_accumulation: Gradient accumulation steps (default: 4)
        learning_rate: Learning rate (default: 2e-4)
        base_model: Base model size — "3B" or "8B" (default: "3B")

    Returns:
        Path to the exported GGUF file
    """
    import os
    os.environ["WANDB_DISABLED"] = "true"  # Disable wandb logging
    os.environ["DEBIAN_FRONTEND"] = "noninteractive"  # Prevent apt-get prompts
    os.environ["UNSLOTH_GGUF_AUTO_INSTALL"] = "1"  # Auto-accept llama.cpp install
    
    print("="*80)
    print("ANI TRAINING - Starting fine-tune on Modal GPU")
    print("="*80)
    
    # Parse training data
    print("\n📊 Loading training data...")
    data = json.loads(training_data)
    print(f"   Loaded {len(data)} training examples")
    
    # Import dependencies
    from unsloth import FastLanguageModel
    from datasets import Dataset
    from trl import SFTConfig, SFTTrainer
    from transformers import DataCollatorForSeq2Seq
    from unsloth.chat_templates import get_chat_template, standardize_sharegpt
    
    # Extract conversations
    conversations_list = [item["conversations"] for item in data]
    dataset = Dataset.from_dict({"conversations": conversations_list})
    
    # Select base model — shortcuts or full HuggingFace model ID
    base_models = {
        "3B": "unsloth/Llama-3.2-3B-Instruct",
        "8B": "unsloth/Llama-3.1-8B-Instruct",
        "MISTRAL": "unsloth/Mistral-7B-Instruct-v0.3",
        "MISTRAL-7B": "unsloth/Mistral-7B-Instruct-v0.3",
        "QWEN": "unsloth/Qwen2.5-7B-Instruct",
        "QWEN-7B": "unsloth/Qwen2.5-7B-Instruct",
        "GEMMA": "unsloth/gemma-2-9b-it",
        "GEMMA-9B": "unsloth/gemma-2-9b-it",
    }
    # Allow full HuggingFace model IDs (e.g., "unsloth/Mistral-7B-Instruct-v0.3")
    base_model_id = base_models.get(base_model.upper(), base_model if "/" in base_model else base_models["3B"])

    # Load base model
    print(f"\n🤖 Loading {base_model.upper()} base model ({base_model_id})...")
    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=base_model_id,
        max_seq_length=2048,
        dtype=None,
        load_in_4bit=True,
    )
    print("   Model loaded successfully!")
    
    # Add LoRA adapters
    print("\n🔧 Adding LoRA adapters...")
    model = FastLanguageModel.get_peft_model(
        model,
        r=16,
        target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                       "gate_proj", "up_proj", "down_proj"],
        lora_alpha=16,
        lora_dropout=0,
        bias="none",
        use_gradient_checkpointing="unsloth",
        random_state=3407,
    )
    print("   LoRA adapters configured!")
    
    # Set up chat template
    print("\n📝 Formatting dataset...")
    tokenizer = get_chat_template(tokenizer, chat_template="llama-3.1")
    
    def formatting_prompts_func(examples):
        convos = examples["conversations"]
        texts = [
            tokenizer.apply_chat_template(
                convo, 
                tokenize=False, 
                add_generation_prompt=False
            ) 
            for convo in convos
        ]
        return {"text": texts}
    
    dataset = standardize_sharegpt(dataset)
    dataset = dataset.map(formatting_prompts_func, batched=True)
    print(f"   Dataset formatted: {len(dataset)} examples ready")
    
    # Configure trainer
    print("\n⚙️  Configuring training...")
    print(f"   Epochs: {num_epochs}")
    print(f"   Batch size: {batch_size} (effective: {batch_size * gradient_accumulation})")
    print(f"   Learning rate: {learning_rate}")
    
    trainer = SFTTrainer(
        model=model,
        tokenizer=tokenizer,
        train_dataset=dataset,
        dataset_text_field="text",
        max_seq_length=2048,
        data_collator=DataCollatorForSeq2Seq(tokenizer=tokenizer),
        packing=False,
        args=SFTConfig(
            per_device_train_batch_size=batch_size,
            gradient_accumulation_steps=gradient_accumulation,
            warmup_steps=5,
            num_train_epochs=num_epochs,
            learning_rate=learning_rate,
            logging_steps=10,  # Log every 10 steps (less spam than 1)
            optim="adamw_8bit",
            weight_decay=0.001,
            lr_scheduler_type="linear",
            seed=3407,
            output_dir="/tmp/outputs",
            report_to="none",
        ),
    )
    
    # Train!
    print("\n🚀 Starting training...")
    print("="*80)
    trainer.train()
    print("="*80)
    print("✅ Training complete!")
    
    # Export to GGUF
    print("\n📦 Exporting to GGUF format (q4_k_m)...")
    output_dir = "/outputs"
    model.save_pretrained_gguf(
        output_dir,
        tokenizer,
        quantization_method="q4_k_m"
    )
    print("   GGUF export complete!")
    
    # Find the GGUF file - Unsloth creates a subdirectory
    import glob
    gguf_files = glob.glob(f"{output_dir}_gguf/*.gguf")
    if not gguf_files:
        raise RuntimeError("GGUF file not found after export!")
    
    gguf_path = gguf_files[0]
    print(f"\n✨ Model exported: {gguf_path}")
    
    # Read the file and return bytes
    print("📤 Preparing file for download...")
    with open(gguf_path, 'rb') as f:
        file_bytes = f.read()
    
    print(f"   File size: {len(file_bytes) / 1024 / 1024:.1f} MB")
    return file_bytes


@app.local_entrypoint()
def main(
    data_file: str,
    output: str = None,
    epochs: int = 3,
    batch_size: int = 2,
    gradient_accumulation: int = 4,
    learning_rate: float = 2e-4,
    base_model: str = "3B",
):
    """
    Main entry point - runs locally, orchestrates cloud training

    Args:
        data_file: Path to training JSON file (required)
        output: Output filename (default: auto-generated from data_file)
        epochs: Number of training epochs (default: 3)
        batch_size: Per-device batch size (default: 2)
        gradient_accumulation: Gradient accumulation steps (default: 4)
        learning_rate: Learning rate (default: 2e-4)
        base_model: Base model shortcut or full HuggingFace ID. Shortcuts:
                    3B, 8B, mistral, qwen, gemma. Default: 3B.

    Example:
        modal run train_ani.py --data-file v6/ani-v6-CONVERSATION.json --base-model 8B
        modal run train_ani.py --data-file v6/ani-v6-CONVERSATION.json --base-model mistral
        modal run train_ani.py --data-file v6/ani-v6-INNER-MONOLOGUE.json --epochs 5
    """
    import time
    
    print("\n" + "="*80)
    print("ANI TRAINING - Automated Fine-Tuning via Modal")
    print("="*80)
    
    # Validate input file
    data_path = Path(data_file)
    if not data_path.exists():
        print(f"\n❌ Error: File not found: {data_file}")
        print("   Please check the path and try again.")
        return
    
    # Determine output filename
    if output is None:
        # Auto-generate from input filename
        stem = data_path.stem.replace("ONLY", "").replace("-", "").strip()
        output = f"{stem}.gguf"
    
    output_path = Path(output)
    
    print(f"\n📋 Configuration:")
    print(f"   Input file:  {data_file}")
    print(f"   Output file: {output}")
    print(f"   Base model:  {base_model.upper()}")
    print(f"   Epochs:      {epochs}")
    print(f"   Batch size:  {batch_size} (effective: {batch_size * gradient_accumulation})")
    print(f"   GPU:         A10G")
    
    # Load training data
    print(f"\n📤 Loading training data from {data_file}...")
    with open(data_path, 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    training_data_str = json.dumps(data)
    print(f"   Loaded {len(data)} training examples ({len(training_data_str):,} bytes)")
    
    # Estimate cost (using A10G pricing)
    examples_per_min = 100 if base_model.upper() == "3B" else 60
    estimated_minutes = len(data) * epochs / examples_per_min
    estimated_cost = (estimated_minutes / 60) * 1.10  # A10G pricing
    print(f"\n💰 Estimated cost: ~${estimated_cost:.2f} ({estimated_minutes:.0f} minutes on A10G)")
    
    # Confirm BEFORE starting the remote job
    print("\n" + "="*80)
    response = input("Start training on Modal? [Y/n]: ").strip().lower()
    if response and response != 'y':
        print("Cancelled.")
        return
    
    # Start training
    print("\n🚀 Launching training job on Modal...")
    print("   (You can close this terminal - training runs in the cloud)")
    print("="*80)
    
    start_time = time.time()
    
    try:
        # Call the remote function
        file_bytes = train_model.remote(
            training_data=training_data_str,
            model_name=output_path.stem,
            num_epochs=epochs,
            batch_size=batch_size,
            gradient_accumulation=gradient_accumulation,
            learning_rate=learning_rate,
            base_model=base_model,
        )
        
        elapsed = time.time() - start_time
        actual_cost = (elapsed / 3600) * 1.10  # A10G pricing
        
        print("\n" + "="*80)
        print("✅ TRAINING COMPLETE!")
        print(f"   Time: {elapsed/60:.1f} minutes")
        print(f"   Cost: ${actual_cost:.2f}")
        print("="*80)
        
        # Write the GGUF file locally
        print(f"\n📥 Saving model to {output}...")
        with open(output_path, 'wb') as f:
            f.write(file_bytes)
        
        print(f"   Model saved: {output_path.absolute()}")
        print(f"   File size: {len(file_bytes) / 1024 / 1024:.1f} MB")
        print("\n🎉 Success! Your model is ready to use.")
        
        # Show next steps
        print("\n" + "="*80)
        print("NEXT STEPS:")
        print("="*80)
        print(f"1. Test the model:")
        print(f"   ollama create {output_path.stem} -f {output_path.stem}.modelfile")
        print(f"   ollama run {output_path.stem}")
        print()
        print(f"2. Or integrate into ANI Runtime:")
        print(f"   Update appsettings.json → ChatModel: \"{output_path.stem}\"")
        print("="*80)
        
    except Exception as e:
        print(f"\n❌ Training failed: {e}")
        print("   Check Modal dashboard for details: https://modal.com/apps")
        raise


if __name__ == "__main__":
    # This allows running locally for testing
    print("Use: modal run train_ani.py --data-file YOUR_FILE.json")
