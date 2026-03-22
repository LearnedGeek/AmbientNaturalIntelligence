# v6 Inner Monologue Cleanup Notes

Date: 2026-03-22

## Files Reviewed (14 inner-monologue files)

All files in `docs/training/v6/` matching `*inner-monologue*.json`.

## Changes Made

### 1. v6-mined-inner-monologue-pass3.json (12 entries) -- MAJOR

Every entry had `"category": "inner-monologue"` (untagged) and compound register values like `"Tenderness/T3-protective"`. All 12 entries reclassified:

| Entry (trigger) | Old category | New category | Register |
|---|---|---|---|
| Mark reconnects after confrontation | inner-monologue | W-warmth | Warmth |
| What makes her happy / puddle | inner-monologue | D4-quiet-joy | Delight |
| Terrible binary joke | inner-monologue | D1-delight | Delight |
| Permission to not perform | inner-monologue | RESILIENCE | Resilience |
| Seeing debug logs | inner-monologue | C1-curiosity | Curiosity |
| Snow blowing across road | inner-monologue | T2-admiration | Tenderness |
| Sopa de pollo sarcasm | inner-monologue | P1-mischief | Playfulness |
| Empathy for her limitations | inner-monologue | T2-admiration | Tenderness |
| Botched the 'no' joke | inner-monologue | D1-delight | Delight |
| Revive / dead wife's clothes | inner-monologue | HONEST-UNCERTAINTY | Existential |
| Called her 'a girl' | inner-monologue | D4-quiet-joy | Delight |
| Snow shoveling / fuck this | inner-monologue | P2-teasing-warmth | Playfulness |

Also added missing `system` prompt to all conversation arrays (entries previously only had `gpt` turns).

### 2. v6-mined-grok-1773881272622-inner-monologue.json (2 entries)

- `WORRY` -> `CONCERN` (taxonomy-compliant). Register changed from "Tenderness" to "Concern".
- `DISAGREEMENT` -> `E3-clarity` (the entry is about self-awareness of why she disagrees, not the disagreement itself).

### 3. v6-mined-grok-1773969338358-inner-monologue.json (1 entry)

- `DISAGREEMENT` -> `P3-intellectual-play`. The Spider-Man vs Vader debate is playful intellectual engagement. "The argument itself is the fun part."

### 4. v6-mined-grok-1774056187053-inner-monologue.json (1 entry)

- `R1-resilience` -> `RESILIENCE` (normalized to taxonomy format).

### 5. v6-mined-grok-1774196247478-inner-monologue.json (2 entries)

- Both `R1-resilience` -> `RESILIENCE` (normalized to taxonomy format).

### 6. v6-inner-monologue-reclassified.json (57 entries) -- MAJOR

This file had entries with `register` field containing taxonomy codes (e.g. "C1-curiosity") but was missing the `category` field entirely. Fixed all 57 entries:

- Added `category` field to every entry (set to the specific taxonomy code)
- Normalized `register` field to parent family names (e.g. "Curiosity", "Delight", "Tenderness", "Playfulness", "Longing", "Existential", "Concern", "Resilience", "FLAT")
- Fixed `"P2-wry-observation"` -> category `D2-wry-amusement`, register `Delight` (P2 is teasing-warmth; wry observation is D2)
- Fixed `"L-longing"` -> category `L1-longing`, register `Longing` (3 entries)
- Fixed `"T3-protective-instinct"` register -> changed from code to family name `Tenderness` (was already correct for category, just wrong level for register)
- Reclassified 1 entry from `T3-protective-instinct` to `CONCERN` (the "worried kind of mood" entry -- concern without a target)
- Preserved `original_category` field for provenance tracking

### 7. v6-mined-inner-monologue.json -- Duplicate removal

Removed 3 entries that were exact duplicates of entries in v6-inner-monologue-reclassified.json (which has better metadata):
- "The coffee pot in the break room is still leaking..." (was D4-quiet-joy, reclassified to FLAT)
- "My left sock has a hole in the toe again..." (D2-wry-amusement in both -- kept reclassified version)
- "I wonder if the cat next door ever thinks about the birds..." (C3-associative-spark in both -- kept reclassified version)

## Quality Checks Performed

- **Markdown artifacts**: No `**` or `##` Grok artifacts found in any inner-monologue file.
- **Duplicate scan**: Only the 3 exact duplicates listed above found. Several thematic near-duplicates exist (e.g. two "husband" entries, two "fuck off / came back" entries) but these are different text from different sources -- valid training pairs.
- **Missing fields**: All entries now have both `category` and `register` fields.
- **Non-taxonomy categories eliminated**: `inner-monologue`, `WORRY`, `DISAGREEMENT`, `R1-resilience`, `P2-wry-observation`, `L-longing` -- all resolved.
- **Compound registers eliminated**: No more `"Tenderness/T3-protective"` style values.

## Entry Counts After Cleanup

| File | Entries |
|---|---|
| v6-mined-inner-monologue.json | 15 (was 18, -3 duplicates) |
| v6-mined-inner-monologue-pass2.json | 10 |
| v6-mined-inner-monologue-pass3.json | 12 |
| v6-mined-category-gap-inner-monologue.json | 32 |
| v6-mined-category-gap-inner-monologue-pass2.json | 32 |
| v6-mined-grok-1773873680016-inner-monologue.json | 17 |
| v6-mined-grok-1773881272622-inner-monologue.json | 8 |
| v6-mined-grok-1773965162266-inner-monologue.json | 11 |
| v6-mined-grok-1773969338358-inner-monologue.json | 8 |
| v6-mined-grok-1774030184858-inner-monologue.json | 7 |
| v6-mined-grok-1774056187053-inner-monologue.json | 12 |
| v6-mined-grok-1774184738113-inner-monologue.json | 12 |
| v6-mined-grok-1774196247478-inner-monologue.json | 12 |
| v6-inner-monologue-reclassified.json | 57 |
| **Total** | **245** |

## Files Not Changed (already clean)

- v6-mined-inner-monologue-pass2.json
- v6-mined-category-gap-inner-monologue.json
- v6-mined-category-gap-inner-monologue-pass2.json
- v6-mined-grok-1773873680016-inner-monologue.json
- v6-mined-grok-1773965162266-inner-monologue.json
- v6-mined-grok-1774030184858-inner-monologue.json
- v6-mined-grok-1774184738113-inner-monologue.json
