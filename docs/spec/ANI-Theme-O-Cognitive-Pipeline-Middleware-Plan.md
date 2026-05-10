# Theme O — Cognitive Pipeline as Middleware

**Status:** PLACEHOLDER drafted May 10, 2026 (10:40 CDT). Architectural framing settled in conversation; Mark directive: *"I almost feel like this Theme O should take priority over other changes because it will become far easier to refactor those others afterwards."*

**Priority claim:** Theme O takes precedence over remaining Theme N work (N.7), R1 Phase 2, and Tier Separation. The argument: every one of those is a "wire something into producers" job today; once Theme O's middleware shape exists, each becomes "register a handler at the right stage" — significantly less work and architecturally consistent. **The cost of doing them first is paid twice — once to wire per-producer, again to refactor onto the pipeline.**

---

## §1 What Theme O is

Cognitive composition restructured as a middleware pipeline matching the ASP.NET Core request-response shape Mark is fluent in:

```
Producer (Outreach / Reply / ReactiveShare / Voice / ...)
    │
    ▼
[ Pre-stage handlers (ordered) ]
    • Frame selection (Theme N)
    • Slice composition (Theme M)
    • Substrate prep (other pre-work)
    │  any handler can short-circuit
    ▼
[ Composition (the middle — LLM call producing message text) ]
    │
    ▼
[ Post-stage handlers (ordered) ]
    • Gate invariants (Theme J)
    • Frame coherence (Theme N N.5)
    • Claim verification (R1)
    • Direct address (existing)
    │  any handler can short-circuit
    ▼
Dispatch (or suppress)
```

Producers collapse from multi-hundred-line orchestration into thin shells: *build context → pipeline.RunAsync(ctx, composeFn) → dispatch on Pass*.

## §2 Empirical motivation

**The architectural failure pattern this closes** (Mark, May 10 10:22 CDT): *"i'm concerned when we say that specific gates are not matching when we talked about universal and not pipeline specific implementations."*

Theme J established the universal `CognitiveOutputGate` for post-composition invariants. The intent was: any producer routes through one shared evaluator. Theme N regressed to per-producer wiring (N.3 in OutreachPhase, N.5 in OutreachPhase, N.6 in OutreachPhase.TryReactiveShareAsync) because the *pre*-composition pattern wasn't consolidated. Each new producer adoption requires duplicating wiring; each new feature requires touching multiple producers. Theme O closes that gap by extending the universal-shape principle to pre-composition handlers and to the orchestration itself.

**Concrete examples of the per-producer drift:**
- N.3 wired selector into `OutreachPhase.RunOutreachAsync` (May 8).
- N.6 wired the same selector again into `OutreachPhase.TryReactiveShareAsync` (May 10) — different method on the same class, different lines of duplication.
- N.7 (`ConversationReplyPhase`) would wire it a third time. Theme O lets N.7 become "populate `artifact.Frame` and the existing pipeline handles the rest."

## §3 The interface shape

```csharp
public interface ICognitivePipelineHandler
{
    PipelineStage Stage { get; }       // Pre or Post
    int           Order { get; }       // intra-stage ordering
    string        Name  { get; }       // for telemetry
    bool          AppliesTo(CognitiveArtifact artifact);
    Task<HandlerResult> HandleAsync(CognitivePipelineContext ctx, CancellationToken ct);
}

public sealed record HandlerResult(
    bool        ShortCircuit,
    DispatchVerdict? Verdict,          // when ShortCircuit, the verdict to return
    string?     Reason);

public sealed class CognitivePipelineContext
{
    public CognitiveArtifact         Artifact      { get; set; } = default!;
    public ContextSnapshot           Snapshot      { get; init; } = default!;
    public OutreachFrame?            Frame         { get; set; }
    public IDictionary<string, object> Bag         { get; } = ...; // handler-extensible state
    public DateTimeOffset            StartedAt     { get; init; }
}

public sealed class CognitivePipeline
{
    public async Task<DispatchResult> RunAsync(
        CognitivePipelineContext ctx,
        Func<CognitivePipelineContext, CancellationToken, Task<string>> composeAsync,
        CancellationToken ct) { ... }
}
```

`CognitiveArtifact` grows an `OutreachFrame? Frame { get; init; }` field so frame information rides with the artifact through the pipeline (post-stage handlers like `FrameCoherenceInvariant` read it).

## §4 What Theme O subsumes

| Existing component | Becomes |
|---|---|
| `ICognitiveOutputInvariant` (Theme J) | `ICognitivePipelineHandler` with `Stage = Post` |
| `CognitiveOutputGate.EvaluateAsync` | The post-stage portion of `CognitivePipeline.RunAsync` |
| `IOutreachFrameSelector` invocation in OutreachPhase / TryReactiveShareAsync (Theme N N.3, N.6) | Pre-stage handler `FrameSelectionHandler`, applies to dispatch-bound producers |
| `IFrameCoherenceChecker` invocation in OutreachPhase (Theme N N.5) | Post-stage handler `FrameCoherenceInvariant`, reads `artifact.Frame` |
| `ClaimVerificationPhase.VerifyAsync` invocation in OutreachPhase | Post-stage handler `ClaimVerificationInvariant` |
| Theme M slice composers (M.1+) | Pre-stage handlers, one per slice |
| Future per-producer additions (N.7 reply-side; voice-side wiring) | Just populate `artifact.Frame`; no new wiring |

The existing `ICognitiveOutputInvariant` interface can survive as a typedef / compatibility shim during migration.

## §5 Telemetry — middleware-style logging requirement

**Mark directive (May 10 10:38 CDT):** *"we should be sure to log well the specific pipelines as middleware to ensure that we treat this like a true middleware."*

Two log levels:

**Pipeline-level (per RunAsync):**
```
O_PIPELINE_START producer=Outreach artifact_id=...
O_PIPELINE_END   producer=Outreach result=Pass duration_ms=347 pre_handlers=4 post_handlers=6
O_PIPELINE_END   producer=Outreach result=ShortCircuit reason="frame-coherence violation" stage=Post handler=FrameCoherenceInvariant
```

**Handler-level (per handler invocation):**
```
O_HANDLER_START stage=Pre handler=FrameSelection producer=Outreach
O_HANDLER_END   stage=Pre handler=FrameSelection producer=Outreach result=Continued duration_ms=12 details="frame=AniInterior score=0.79"
O_HANDLER_END   stage=Post handler=ClaimVerification producer=Outreach result=Continued duration_ms=820 details="3 claims supported"
O_HANDLER_END   stage=Post handler=FrameCoherenceInvariant producer=Outreach result=ShortCircuit duration_ms=2 details="violation: \"we both wanted\""
```

This matches the ASP.NET Core diagnostic-source pattern + lets observability tooling (or just `Select-String` over log files) reconstruct any cycle's exact pipeline flow. Log volume increase is real but tunable per handler-importance; the `details` field is a per-handler structured string with whatever the handler wants to surface.

**Acceptance criterion:** Mark must be able to read a log file and answer the question *"what pipeline ran for the 09:36 outreach, in what order, and where did it short-circuit?"* in under 30 seconds via grep. Today that question is hard because gates / selector / verifier log inconsistently with no shared trace identifier.

## §6 Phase plan

### O.0 — Plan-doc finalization + interface design
- This document → full plan with locked interfaces.
- Interface review with Mark before O.1 starts.
- **Estimated:** 1 day.

### O.1 — `ICognitivePipelineHandler` + `CognitivePipeline` infrastructure
- New interfaces + `CognitivePipeline` orchestrator class.
- `CognitivePipelineContext` + `HandlerResult` + `DispatchResult` value types.
- Telemetry (`O_PIPELINE_*` + `O_HANDLER_*`) baked into the orchestrator, not per-handler.
- DI registration scaffold.
- Spec tests for the orchestrator (handler ordering, short-circuit propagation, AppliesTo filtering, exception handling).
- **No producer migrations yet.**
- **Estimated:** 2 days.

### O.2 — Migrate Theme J invariants to post-stage handlers
- Existing `ICognitiveOutputInvariant` implementations adapted to `ICognitivePipelineHandler` (typedef shim or thin adapter).
- The current `CognitiveOutputGate.EvaluateAsync` becomes a pass-through to the post-stage of `CognitivePipeline`.
- All existing invariant tests must still pass.
- **Estimated:** 1.5 days.

### O.3 — Migrate Theme N selector + frame-coherence to handlers
- `FrameSelectionHandler` (pre-stage).
- `FrameCoherenceInvariant` (post-stage handler reading `artifact.Frame`).
- Add `OutreachFrame? Frame` to `CognitiveArtifact`.
- N.3 + N.5 OutreachPhase wiring removed; replaced by the producer just calling pipeline.
- **Estimated:** 1.5 days.

### O.4 — Migrate OutreachPhase to use pipeline
- `RunOutreachAsync` collapsed to: build ctx → pipeline.RunAsync → dispatch.
- Same for `TryReactiveShareAsync` (N.6 wiring removed; same pre-stage handler applies via `AppliesTo`).
- Spec tests verify behavior parity with current (post-N.6) state.
- **Estimated:** 1.5 days.

### O.5 — Migrate ConversationReplyPhase (replaces what would have been Theme N N.7)
- Same shape. Reply-path producers become thin shells.
- Frame selection now applies to replies automatically (configurable per-frame `AppliesTo`).
- The feet-confab failure class from May 10 10:15 closes architecturally — reply-path is now under the same pipeline as outreach.
- **Estimated:** 1.5 days.

### O.6 — Migrate reactive-share + Voice producers
- ReactiveShare path was already migrated as part of OutreachPhase (O.4) since it lives there.
- Voice path: VoiceTurnPipeline rewritten to use the cognitive pipeline. The Option-1 pre-stream gate finding from `docs/spec/findings/2026-05-07-voice-safeack-bypass.md` becomes natural: voice's composition is "stream tokens to TTS"; the post-stage runs after a buffered full reply, with cancel-pending-tts on short-circuit.
- **Estimated:** 2 days.

### O.7 — Decommission per-producer wiring; pipeline is the single shape
- Remove deprecated invocation paths (the original `_outputGate.EvaluateAsync` direct calls, the `_frameSelector.SelectFrameAsync` direct calls in producers, etc.).
- Delete compatibility shims from O.2.
- Final spec test sweep to confirm all producers route through pipeline and no per-producer wiring remains.
- Paper 3 contribution writeup: *"Cognitive pipeline as middleware — generalized request-response architecture for AI composition systems."* Adds to the architecture-over-instruction principle as the orchestration-side answer.
- **Estimated:** 1 day.

**Total Theme O effort:** ~10-12 working days (1.5-2 weeks calendar). Compounding benefit: every future Theme N phase, R1 phase, Theme M phase becomes "register a handler" — saves multiples of that effort over the next 1-2 months.

## §7 Migration safety

- O.1 ships infrastructure with NO producer changes — fully reversible until O.4.
- O.2 ships compatibility shim so existing invariant tests pass without modification.
- O.4 + O.5 + O.6 each ship one producer at a time, under a feature flag (`UseCognitivePipeline_Outreach` etc.) so each can be canary-flipped independently. The pre-pipeline code path stays in place during canary.
- O.7 only deletes legacy code after each producer has been on the pipeline path for ≥1 week with no regressions.

## §8 What blocks behind Theme O

Items that should wait for Theme O to land before being implemented (because each saves work by being added as a handler instead of producer wiring):

- **Theme N N.7** — ConversationReplyPhase frame-aware composition. Becomes free in O.5.
- **R1 Phase 2** — extending typed-claim verification beyond `shared-event-with-attribution`. Becomes a single handler refinement instead of producer-by-producer.
- **Tier Separation rollout** — minor; some retrieval changes happen at handler level cleanly.
- **Voice pipeline SafeAck-bypass fix** — the Option-1 pre-stream gate is naturally the post-stage of a voice pipeline. Lands as O.6.

Items that DON'T block on Theme O:
- Theme M.2 telemetry build-out — lives in slice composers, mostly orthogonal.
- Theme G Layer 4 corpus work — training-side, completely separate.
- v8 readiness — training-side.
- Anything purely doc / paper-facing.

## §9 Interface decisions — LOCKED May 10, 2026 18:06 CDT

All four open questions resolved with Mark in conversation. Locks below are binding for O.1 interface design.

1. **🔒 Composition as the implicit middle** (NOT as a handler). Producer passes `composeAsync` into `RunAsync`; pipeline runs Pre handlers, then `composeAsync`, then Post handlers. Mark: *"composition intuitively seems like the 'middle' so that makes sense."* Revisit only if multiple producers want to share composition logic.

2. **🔒 Single pipeline + `AppliesTo` filtering** (NOT per-producer pipelines). One `CognitivePipeline` instance, all handlers registered, each handler's `AppliesTo(artifact)` predicate decides whether it fires for a given producer-kind / sink. Mark: *"yes, single pipeline for now. let's hope that sticks."* The hope-it-sticks framing is honest — if `AppliesTo` proves too coarse for future producer differentiation, revisit at that boundary.

3. **🔒 Fluent `app.Use`-style ordering at registration.** No `Order` integers, no DI-discovery magic. Pipeline shape is defined in one place (`Program.cs`) using a fluent builder:
   ```csharp
   builder.Services.AddCognitivePipeline(p => p
       .UsePreHandler<FrameSelectionHandler>()
       .UsePreHandler<SliceCompositionHandler>()
       // composition is the implicit middle
       .UsePostHandler<FrameCoherenceInvariant>()
       .UsePostHandler<ClaimVerificationInvariant>()
       .UsePostHandler<SelfEchoInvariant>()
       .UsePostHandler<DirectAddressInvariant>()
   );
   ```
   Mark: *"app.Use? That's fine and typical."* Registration order = execution order. The whole pipeline shape is grep-able in one place; reorders are a one-line edit at the call site, not a hunt across `Order` integer values.

4. **🔒 Typed `CognitivePipelineContext` + type-keyed state accessors** (NOT a generic-object `Bag`, NOT chained handler-return objects). Explicit typed properties for load-bearing common data; type-safe state-bag API for handler-extensible state:
   ```csharp
   public sealed class CognitivePipelineContext
   {
       // Explicit typed properties for common data
       public CognitiveArtifact   Artifact  { get; set; } = default!;
       public ContextSnapshot     Snapshot  { get; init; } = default!;
       public OutreachFrame?      Frame     { get; set; }
       public string?             ComposedContent { get; set; }
       public DateTimeOffset      StartedAt { get; init; }

       // Type-safe handler-extensible state (no generic-object Bag)
       public T?   GetState<T>() where T : class;
       public void SetState<T>(T state) where T : class;
   }
   ```
   Mark: *"the Bag gets to be a mess with a generic object."* Type-keyed dictionary internally, type-safe API externally. Handler A: `ctx.SetState(new FrameSelectionTelemetry(...))`. Handler B: `var tel = ctx.GetState<FrameSelectionTelemetry>()`. No string keys, no `object` casting, no fishing.

   **Rejected alternative: chained handler-return objects** (each handler returns a typed result that feeds into the next). That pattern creates ordering coupling — handler B has to know handler A's output type, which means rearranging the pipeline requires rewriting signatures. The shared typed context keeps ordering decoupled from handler signatures, preserving the whole point of being able to reorder via the fluent `app.Use` builder.

**All four locks confirmed.** O.1 starts with these interface decisions baked in.

## §10 Status log

- **2026-05-10 (10:22 CDT)** — Mark flagged the per-pipeline regression: *"i'm concerned when we say that specific gates are not matching when we talked about universal and not pipeline specific implementations."* Theme N's per-producer wiring identified as architectural drift from Theme J's universal-gate intent.
- **2026-05-10 (10:31 CDT)** — Mark proposed the HTTP middleware pattern as the architectural answer. *"can we do something like that?"* Sketch agreed in conversation.
- **2026-05-10 (10:38 CDT)** — Mark prioritized Theme O over other changes: *"this Theme O should take priority over other changes because it will become far easier to refactor those others afterwards."* Plus middleware-logging directive: *"log well the specific pipelines as middleware to ensure that we treat this like a true middleware."*
- **2026-05-10 (10:40 CDT)** — Placeholder plan-doc drafted (this document). Awaiting Mark's interface review before O.1 starts.
- **2026-05-10 (18:06 CDT)** — All four §9 interface decisions locked. Composition as implicit middle ✓; single pipeline + AppliesTo ✓; fluent `app.Use`-style ordering ✓; typed Context + type-keyed state accessors ✓ (Bag and chained-handler-return both rejected). O.1 starts with these baked in.

**NEXT** — O.1 begins. `ICognitivePipelineHandler` + `CognitivePipeline` orchestrator + fluent builder + telemetry skeleton + spec tests. No producer migrations yet.
