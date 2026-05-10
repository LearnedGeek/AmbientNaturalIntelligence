using System.Diagnostics;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using DispatchVerdict = AniRuntime.Core.Models.OutputGateVerdict;

namespace AniRuntime.Loops.Pipeline;

/// <summary>
/// Theme O Phase O.1 (May 10, 2026) — middleware orchestrator. Runs Pre
/// handlers in registration order, then the producer-supplied composition
/// delegate, then Post handlers in registration order. Any handler can
/// short-circuit; the orchestrator surfaces the short-circuit handler name
/// + stage on the returned <see cref="DispatchResult"/>.
///
/// **Telemetry**: emits structured log lines matching
/// <c>docs/spec/ANI-Theme-O-Cognitive-Pipeline-Middleware-Plan.md</c> §5:
/// <c>O_PIPELINE_START</c>, <c>O_PIPELINE_END</c>, <c>O_HANDLER_START</c>,
/// <c>O_HANDLER_END</c>, <c>O_COMPOSITION_START</c>, <c>O_COMPOSITION_END</c>.
/// The shapes are stable + grep-able so the acceptance criterion ("answer
/// what pipeline ran for the 09:36 outreach in under 30 seconds") holds.
///
/// **Exception safety**: a handler that throws is logged at Warning and
/// treated as a short-circuit Fail. The composition delegate itself is NOT
/// wrapped — composition exceptions bubble to the caller, who already has
/// pre-existing error handling for LLM-call failures.
/// </summary>
public sealed class CognitivePipeline
{
    private readonly IReadOnlyList<ICognitivePipelineHandler> _preHandlers;
    private readonly IReadOnlyList<ICognitivePipelineHandler> _postHandlers;
    private readonly ILogger<CognitivePipeline>               _log;

    public CognitivePipeline(
        IEnumerable<ICognitivePipelineHandler> handlers,
        ILogger<CognitivePipeline>             log)
    {
        // Preserve registration order; split by stage at construction so we
        // don't re-filter on every RunAsync call.
        var ordered = handlers?.ToList() ?? new List<ICognitivePipelineHandler>();
        _preHandlers  = ordered.Where(h => h.Stage == PipelineStage.Pre).ToList();
        _postHandlers = ordered.Where(h => h.Stage == PipelineStage.Post).ToList();
        _log          = log;
    }

    /// <summary>
    /// Run the pipeline against <paramref name="ctx"/>. Pre handlers fire
    /// first; if none short-circuits, <paramref name="composeAsync"/> runs
    /// and its result is assigned to <see cref="CognitivePipelineContext.ComposedContent"/>
    /// + <see cref="CognitiveArtifact.Content"/>; then Post handlers fire.
    ///
    /// Returns a <see cref="DispatchResult"/> with verdict + (if applicable)
    /// the short-circuiting handler's name + stage.
    /// </summary>
    public async Task<DispatchResult> RunAsync(
        CognitivePipelineContext                                          ctx,
        Func<CognitivePipelineContext, CancellationToken, Task<string>>   composeAsync,
        CancellationToken                                                 ct)
    {
        if (ctx           is null) throw new ArgumentNullException(nameof(ctx));
        if (composeAsync  is null) throw new ArgumentNullException(nameof(composeAsync));
        if (ctx.Artifact  is null) throw new ArgumentException("ctx.Artifact must be populated before RunAsync.", nameof(ctx));

        var producer = ctx.Artifact.ProducerKind;
        var artifactId = ctx.Artifact.GeneratedAt.ToUnixTimeMilliseconds();
        var pipelineSw = Stopwatch.StartNew();

        _log.LogInformation(
            "O_PIPELINE_START producer={Producer} artifact_id={ArtifactId} mode={Mode}",
            producer, artifactId, "Full");

        // ── Pre stage ─────────────────────────────────────────────────────
        var preRan = 0;
        foreach (var handler in _preHandlers)
        {
            if (ct.IsCancellationRequested)
            {
                return EmitEndShortCircuit(producer, pipelineSw, preRan, postRan: 0,
                    stage: PipelineStage.Pre, handler: "<cancelled>", reason: "cancellation requested");
            }

            if (!handler.AppliesTo(ctx.Artifact)) continue;

            preRan++;
            var (result, hadException) = await InvokeHandlerAsync(handler, ctx, ct).ConfigureAwait(false);
            if (result.ShortCircuit)
            {
                return EmitEndShortCircuit(producer, pipelineSw, preRan, postRan: 0,
                    stage: PipelineStage.Pre, handler: handler.Name,
                    reason: result.Reason ?? (hadException ? "handler threw" : "short-circuit"),
                    verdict: result.Verdict ?? DispatchVerdict.Fail);
            }
        }

        // ── Composition (implicit middle) ─────────────────────────────────
        _log.LogInformation("O_COMPOSITION_START producer={Producer}", producer);
        var compSw = Stopwatch.StartNew();
        var composed = await composeAsync(ctx, ct).ConfigureAwait(false);
        compSw.Stop();
        ctx.ComposedContent = composed;
        // Update the artifact's Content so post-stage handlers (especially
        // Theme J invariants) see the composed text on the artifact itself.
        ctx.Artifact = CloneArtifactWithContent(ctx.Artifact, composed);

        _log.LogInformation(
            "O_COMPOSITION_END producer={Producer} duration_ms={Duration} content_length={Length}",
            producer, compSw.ElapsedMilliseconds, composed?.Length ?? 0);

        // ── Post stage ────────────────────────────────────────────────────
        var postRanCount = 0;
        foreach (var handler in _postHandlers)
        {
            if (ct.IsCancellationRequested)
            {
                return EmitEndShortCircuit(producer, pipelineSw, preRan, postRanCount,
                    stage: PipelineStage.Post, handler: "<cancelled>", reason: "cancellation requested");
            }

            if (!handler.AppliesTo(ctx.Artifact)) continue;

            postRanCount++;
            var (result, hadException) = await InvokeHandlerAsync(handler, ctx, ct).ConfigureAwait(false);
            if (result.ShortCircuit)
            {
                return EmitEndShortCircuit(producer, pipelineSw, preRan, postRanCount,
                    stage: PipelineStage.Post, handler: handler.Name,
                    reason: result.Reason ?? (hadException ? "handler threw" : "short-circuit"),
                    verdict: result.Verdict ?? DispatchVerdict.Fail);
            }
        }

        // ── Pass ──────────────────────────────────────────────────────────
        pipelineSw.Stop();
        _log.LogInformation(
            "O_PIPELINE_END producer={Producer} result=Pass duration_ms={Duration} pre_handlers={Pre} post_handlers={Post}",
            producer, pipelineSw.ElapsedMilliseconds, preRan, postRanCount);

        return DispatchResult.Pass();
    }

    /// <summary>
    /// Theme O Phase O.2 (May 10, 2026) — pass-through for callers that
    /// operate on already-composed artifacts (the legacy
    /// <see cref="ICognitiveOutputGate"/> evaluator path). Skips Pre handlers
    /// AND composition entirely; runs Post handlers only against the artifact
    /// already populated on <paramref name="ctx"/>.
    ///
    /// Telemetry shape matches <see cref="RunAsync"/> with
    /// <c>mode=PostOnly</c> on the <c>O_PIPELINE_START</c> line so log
    /// readers can distinguish the two evaluation modes; short-circuit +
    /// <see cref="ICognitivePipelineHandler.AppliesTo"/> semantics are
    /// identical.
    /// </summary>
    public async Task<DispatchResult> RunPostOnlyAsync(
        CognitivePipelineContext ctx,
        CancellationToken        ct)
    {
        if (ctx           is null) throw new ArgumentNullException(nameof(ctx));
        if (ctx.Artifact  is null) throw new ArgumentException("ctx.Artifact must be populated before RunPostOnlyAsync.", nameof(ctx));

        var producer = ctx.Artifact.ProducerKind;
        var artifactId = ctx.Artifact.GeneratedAt.ToUnixTimeMilliseconds();
        var pipelineSw = Stopwatch.StartNew();

        _log.LogInformation(
            "O_PIPELINE_START producer={Producer} artifact_id={ArtifactId} mode={Mode}",
            producer, artifactId, "PostOnly");

        // ── Post stage only ───────────────────────────────────────────────
        var postRanCount = 0;
        foreach (var handler in _postHandlers)
        {
            if (ct.IsCancellationRequested)
            {
                return EmitEndShortCircuit(producer, pipelineSw, preRan: 0, postRanCount,
                    stage: PipelineStage.Post, handler: "<cancelled>", reason: "cancellation requested");
            }

            if (!handler.AppliesTo(ctx.Artifact)) continue;

            postRanCount++;
            var (result, hadException) = await InvokeHandlerAsync(handler, ctx, ct).ConfigureAwait(false);
            if (result.ShortCircuit)
            {
                return EmitEndShortCircuit(producer, pipelineSw, preRan: 0, postRanCount,
                    stage: PipelineStage.Post, handler: handler.Name,
                    reason: result.Reason ?? (hadException ? "handler threw" : "short-circuit"),
                    verdict: result.Verdict ?? DispatchVerdict.Fail);
            }
        }

        // ── Pass ──────────────────────────────────────────────────────────
        pipelineSw.Stop();
        _log.LogInformation(
            "O_PIPELINE_END producer={Producer} result=Pass duration_ms={Duration} pre_handlers={Pre} post_handlers={Post}",
            producer, pipelineSw.ElapsedMilliseconds, 0, postRanCount);

        return DispatchResult.Pass();
    }

    private async Task<(HandlerResult Result, bool HadException)> InvokeHandlerAsync(
        ICognitivePipelineHandler handler,
        CognitivePipelineContext  ctx,
        CancellationToken         ct)
    {
        var stage    = handler.Stage;
        var producer = ctx.Artifact.ProducerKind;

        _log.LogInformation(
            "O_HANDLER_START stage={Stage} handler={Handler} producer={Producer}",
            stage, handler.Name, producer);

        var sw = Stopwatch.StartNew();
        HandlerResult result;
        bool hadException = false;
        try
        {
            result = await handler.HandleAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            _log.LogInformation(
                "O_HANDLER_END stage={Stage} handler={Handler} producer={Producer} result=ShortCircuit duration_ms={Duration} details=\"cancelled\"",
                stage, handler.Name, producer, sw.ElapsedMilliseconds);
            return (HandlerResult.ShortCircuitWith(DispatchVerdict.Fail, "cancelled"), false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogWarning(ex,
                "O_HANDLER_END stage={Stage} handler={Handler} producer={Producer} result=ShortCircuit duration_ms={Duration} details=\"exception: {Message}\"",
                stage, handler.Name, producer, sw.ElapsedMilliseconds, ex.Message);
            hadException = true;
            result = HandlerResult.ShortCircuitWith(DispatchVerdict.Fail, $"exception: {ex.Message}");
            return (result, hadException);
        }
        sw.Stop();

        var resultLabel = result.ShortCircuit ? "ShortCircuit" : "Continued";
        _log.LogInformation(
            "O_HANDLER_END stage={Stage} handler={Handler} producer={Producer} result={Result} duration_ms={Duration} details=\"{Details}\"",
            stage, handler.Name, producer, resultLabel, sw.ElapsedMilliseconds, result.Reason ?? string.Empty);

        return (result, hadException);
    }

    private DispatchResult EmitEndShortCircuit(
        CognitiveProducerKind producer,
        Stopwatch             pipelineSw,
        int                   preRan,
        int                   postRan,
        PipelineStage         stage,
        string                handler,
        string                reason,
        DispatchVerdict       verdict = DispatchVerdict.Fail)
    {
        pipelineSw.Stop();
        _log.LogInformation(
            "O_PIPELINE_END producer={Producer} result=ShortCircuit duration_ms={Duration} pre_handlers={Pre} post_handlers={Post} reason=\"{Reason}\" stage={Stage} handler={Handler}",
            producer, pipelineSw.ElapsedMilliseconds, preRan, postRan, reason, stage, handler);

        return new DispatchResult(verdict, reason, handler, stage);
    }

    /// <summary>
    /// Clone the artifact carrying a new <see cref="CognitiveArtifact.Content"/>.
    /// All other init-only properties (ProducerKind, IntendedSink, Frame, etc.)
    /// are preserved. This is the canonical way Post handlers see the
    /// composed text on the artifact they evaluate.
    /// </summary>
    private static CognitiveArtifact CloneArtifactWithContent(
        CognitiveArtifact src, string newContent)
    {
        return new CognitiveArtifact
        {
            Content                  = newContent ?? string.Empty,
            ProducerKind             = src.ProducerKind,
            IntendedSink             = src.IntendedSink,
            ContactName              = src.ContactName,
            GeneratedAt              = src.GeneratedAt,
            ContactRecentMessages    = src.ContactRecentMessages,
            PriorAniMessages         = src.PriorAniMessages,
            SystemPromptText         = src.SystemPromptText,
            WriterInnerThought       = src.WriterInnerThought,
            CanonicalAddresseeNames  = src.CanonicalAddresseeNames,
            Frame                    = src.Frame,
        };
    }
}
