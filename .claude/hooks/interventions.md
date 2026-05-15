# Active behavioral interventions

<!-- SENTINEL: zenith-cobalt-7349 — if Claude sees this exact phrase in
     context, the hook is firing correctly. Remove after verification. -->

These inject into Claude's context on every `UserPromptSubmit` via `on_prompt.ps1`. Each entry is a small, load-bearing question that fires before Claude drafts a response — structurally inserted, not memory-recalled.

Edit freely. The hook re-reads this file every turn.

Keep the list **short and sharp** (3–5 items). Long lists become noise Claude parses past; the value is in surfacing the questions that catch the recurring traps. When a trap stops recurring, retire its question.

---

**Production-failure fix?** Categorize first: **training-side** / **structural** / **gate**. Default-NO to gate. State the category in the response and, if "gate," justify why training and structural alternatives are demonstrably wrong for this case. See `memory/feedback_gate_shaped_default_trap.md`.

**Adding code or extending behavior?** Check `~/.claude/ARCHITECTURE_PATTERNS.md` line 478 for the N-places-drift anti-pattern. Count consumer sites NOW with file+line before claiming "no concrete consumer." See `memory/feedback_check_global_guides_for_solid.md`.

**Surfacing "what's next"?** ONE concrete step, not a buffet of 5+ items. Only acceptable workstream departure: research papers + log status. See `memory/feedback_systematic_completion.md`.

**Current active workstream:** Gate-stack reduction — see `docs/spec/ANI-Gate-Stack-Reduction-Plan.md`. Don't add new gates without explicit Mark redirection. Steps 1, 2a, 2b, 2c, 3 shipped; structural composition fix `3f25d66` shipped on top.
