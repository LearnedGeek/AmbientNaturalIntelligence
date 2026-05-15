# Active behavioral interventions

These inject into Claude's context on every `UserPromptSubmit` via `on_prompt.ps1`. Each entry is a small, load-bearing question that fires before Claude drafts a response — structurally inserted, not memory-recalled.

Edit freely. The hook re-reads this file every turn.

Keep the list **short and sharp** (3–5 items). Long lists become noise Claude parses past; the value is in surfacing the questions that catch the recurring traps. When a trap stops recurring, retire its question.

---

**Production-failure fix?** Categorize first: **training-side** / **structural** / **gate**. Default-NO to gate. State the category in the response and, if "gate," justify why training and structural alternatives are demonstrably wrong for this case. See `memory/feedback_gate_shaped_default_trap.md`.

**Adding code or extending behavior?** Check `~/.claude/ARCHITECTURE_PATTERNS.md` line 478 for the N-places-drift anti-pattern. Count consumer sites NOW with file+line before claiming "no concrete consumer." See `memory/feedback_check_global_guides_for_solid.md`.

**Surfacing "what's next"?** ONE concrete step, not a buffet of 5+ items. Only acceptable workstream departure: research papers + log status. See `memory/feedback_systematic_completion.md`.

**Gate/verifier/consumer remediated or rejected something?** Check the SUBSTRATE/DATA it received BEFORE deciding the consumer's logic or prompt is wrong. If the gate is correctly applying its rule to empty/malformed input, the fix is upstream in data provision — NOT in the consumer's prompt, NOT in the consumer's rule. Empirical anchor: May 15 14:47 SafeAck, where Sonnet correctly remediated on q5 because `MarkAssertedSubstrate` was empty despite 11 caregiver records in the retrieval pool. Pipeline-fix route ("rewrite q5") was the wrong shape; data-feed route is the right one.

**Current active workstream:** Gate-stack reduction — see `docs/spec/ANI-Gate-Stack-Reduction-Plan.md`. Don't add new gates without explicit Mark redirection. Steps 1, 2a, 2b, 2c, 3 shipped; structural composition fix `3f25d66` shipped on top.
