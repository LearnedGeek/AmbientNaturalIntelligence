namespace AniRuntime.Core.Models;

/// <summary>
/// Issue #96 (2026-07-15) — Descriptor for a callable tool the LLM classifier
/// can select. Passed into <see cref="Interfaces.IToolCallClassifier"/> as
/// part of the "world" the classifier sees on each user turn.
///
/// **Why a descriptor separate from the action itself.** The classifier only
/// needs the schema (name / description / parameter shape) to decide *which*
/// tool to call — it does not need a reference to the runnable action. This
/// keeps the classifier fixture-testable without needing to instantiate a
/// dispatcher or wire real actions in.
/// </summary>
/// <param name="Name">Tool identifier used in the classifier's JSON verdict
/// (e.g. <c>"recall_memory"</c>). Snake-case for parity with the JSON shape
/// LLMs emit most reliably.</param>
/// <param name="Description">One-sentence natural-language description of
/// when the tool is appropriate. Shown to the classifier in the system prompt.</param>
/// <param name="ParameterSchema">Map of parameter name to a short description
/// including type. Intentionally not a full JSON Schema — the classifier's job
/// is to emit reasonable arguments, and a compact map keeps the prompt short.
/// Example: <c>{"query": "string — the memory search phrase"}</c>.</param>
public sealed record ToolDescriptor(
    string                            Name,
    string                            Description,
    IReadOnlyDictionary<string, string> ParameterSchema);

/// <summary>
/// Issue #96 (2026-07-15) — Structured verdict returned by
/// <see cref="Interfaces.IToolCallClassifier"/>. Shape mirrors
/// <see cref="TagIntentVerdict"/>: intent-like decision (call vs no-call) +
/// confidence scalar + one-line reason for audit.
///
/// **Substrate-safety note.** The verdict is *classifier output* — imprecise
/// by construction. Callers must treat this as a signal to invoke a
/// deterministic action, never as a factual claim. Writing the verdict's
/// reasoning to substrate would reintroduce the Phase 2.1 anti-pattern
/// (imprecise verdict → deterministic mutation).
/// </summary>
/// <param name="ShouldCallTool"><c>true</c> if the classifier picked a tool;
/// <c>false</c> when the user message doesn't need one (the common case).</param>
/// <param name="ToolName">The picked tool's <see cref="ToolDescriptor.Name"/>
/// when <see cref="ShouldCallTool"/> is <c>true</c>; <c>null</c> otherwise.</param>
/// <param name="Arguments">Extracted arguments keyed by parameter name. Values
/// are <see cref="string"/> by construction — the classifier emits JSON
/// primitives, and the action layer parses them at invocation time.</param>
/// <param name="Confidence">Model-reported confidence in [0.0, 1.0].</param>
/// <param name="Reason">One-line justification. Kept for audit + eval-harness
/// inspection.</param>
public sealed record ToolCallVerdict(
    bool                                 ShouldCallTool,
    string?                              ToolName,
    IReadOnlyDictionary<string, string>? Arguments,
    float                                Confidence,
    string?                              Reason);
