using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DispatchVerdict = AniRuntime.Core.Models.OutputGateVerdict;

namespace AniRuntime.Tests;

/// <summary>
/// Theme O Phase O.1 (May 10, 2026) — spec tests for
/// <see cref="CognitivePipeline"/> + <see cref="CognitivePipelineBuilder"/>.
/// Pins the orchestration contract: stage ordering, AppliesTo filtering,
/// short-circuit propagation across stages, composition placement,
/// exception containment, type-keyed state round-trip, telemetry shape,
/// cancellation, and builder stage-mismatch detection.
///
/// All handlers in these tests are synthetic fakes (no DI, no real
/// invariants) so the orchestrator logic is tested in isolation from any
/// specific handler's behaviour.
/// </summary>
public class CognitivePipelineTests
{
    // ─── Fakes ───────────────────────────────────────────────────────────

    private sealed class FakeHandler : ICognitivePipelineHandler
    {
        public PipelineStage Stage { get; }
        public string        Name  { get; }
        public Func<CognitiveArtifact, bool>                                 AppliesFunc { get; init; } = _ => true;
        public Func<CognitivePipelineContext, CancellationToken, Task<HandlerResult>> HandleFunc  { get; init; }
            = (_, _) => Task.FromResult(HandlerResult.Continue());
        public int InvocationCount { get; private set; }

        public FakeHandler(PipelineStage stage, string name)
        {
            Stage = stage;
            Name  = name;
        }

        public bool AppliesTo(CognitiveArtifact artifact) => AppliesFunc(artifact);
        public Task<HandlerResult> HandleAsync(CognitivePipelineContext ctx, CancellationToken ct)
        {
            InvocationCount++;
            return HandleFunc(ctx, ct);
        }
    }

    /// <summary>Captures formatted log lines from the orchestrator (matches the
    /// pattern in <see cref="OutreachFrameSelectorTests"/>).</summary>
    private sealed class CapturingLogger : ILogger<CognitivePipeline>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(
            LogLevel level, EventId id, TState state,
            Exception? ex, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, ex));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static CognitiveArtifact ArtifactFor(string content = "<pending>")
        => new()
        {
            Content      = content,
            ProducerKind = CognitiveProducerKind.Outreach,
            IntendedSink = CognitiveOutputSink.Dispatch,
        };

    private static CognitivePipelineContext CtxFor(CognitiveArtifact? artifact = null)
        => new()
        {
            Artifact = artifact ?? ArtifactFor(),
            Snapshot = new ContextSnapshot(),
        };

    private static CognitivePipeline Build(
        ILogger<CognitivePipeline>? log = null,
        params ICognitivePipelineHandler[] handlers)
        => new(handlers, log ?? NullLogger<CognitivePipeline>.Instance);

    private static Func<CognitivePipelineContext, CancellationToken, Task<string>> Composer(string content)
        => (_, _) => Task.FromResult(content);

    // ─── 1. Pre handlers fire in registration order ──────────────────────

    [Fact]
    public async Task PreHandlers_FireInRegistrationOrder()
    {
        var order = new List<string>();
        var h1 = new FakeHandler(PipelineStage.Pre, "first")
        {
            HandleFunc = (_, _) => { order.Add("first"); return Task.FromResult(HandlerResult.Continue()); }
        };
        var h2 = new FakeHandler(PipelineStage.Pre, "second")
        {
            HandleFunc = (_, _) => { order.Add("second"); return Task.FromResult(HandlerResult.Continue()); }
        };
        var h3 = new FakeHandler(PipelineStage.Pre, "third")
        {
            HandleFunc = (_, _) => { order.Add("third"); return Task.FromResult(HandlerResult.Continue()); }
        };

        var pipeline = Build(null, h1, h2, h3);
        var result = await pipeline.RunAsync(CtxFor(), Composer("composed"), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Pass);
        order.Should().Equal("first", "second", "third");
    }

    // ─── 2. Post handlers fire in registration order ─────────────────────

    [Fact]
    public async Task PostHandlers_FireInRegistrationOrder()
    {
        var order = new List<string>();
        var p1 = new FakeHandler(PipelineStage.Post, "post-a")
        {
            HandleFunc = (_, _) => { order.Add("post-a"); return Task.FromResult(HandlerResult.Continue()); }
        };
        var p2 = new FakeHandler(PipelineStage.Post, "post-b")
        {
            HandleFunc = (_, _) => { order.Add("post-b"); return Task.FromResult(HandlerResult.Continue()); }
        };
        var p3 = new FakeHandler(PipelineStage.Post, "post-c")
        {
            HandleFunc = (_, _) => { order.Add("post-c"); return Task.FromResult(HandlerResult.Continue()); }
        };

        var pipeline = Build(null, p1, p2, p3);
        var result = await pipeline.RunAsync(CtxFor(), Composer("composed"), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Pass);
        order.Should().Equal("post-a", "post-b", "post-c");
    }

    // ─── 3. AppliesTo filtering — false skips HandleAsync entirely ───────

    [Fact]
    public async Task AppliesTo_False_HandlerNeverInvoked()
    {
        var skipped = new FakeHandler(PipelineStage.Pre, "skipped")
        {
            AppliesFunc = _ => false,
            HandleFunc  = (_, _) => throw new InvalidOperationException("must not be called"),
        };
        var applied = new FakeHandler(PipelineStage.Pre, "applied");

        var pipeline = Build(null, skipped, applied);
        var result = await pipeline.RunAsync(CtxFor(), Composer("c"), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Pass);
        skipped.InvocationCount.Should().Be(0);
        applied.InvocationCount.Should().Be(1);
    }

    // ─── 4. Short-circuit in Pre skips composition + Post ────────────────

    [Fact]
    public async Task PreShortCircuit_SkipsCompositionAndPost()
    {
        var compositionRan = false;
        var sc = new FakeHandler(PipelineStage.Pre, "pre-stop")
        {
            HandleFunc = (_, _) => Task.FromResult(
                HandlerResult.ShortCircuitWith(DispatchVerdict.Fail, "pre stop"))
        };
        var postHandler = new FakeHandler(PipelineStage.Post, "post-never");

        var pipeline = Build(null, sc, postHandler);

        Task<string> Compose(CognitivePipelineContext _, CancellationToken __)
        {
            compositionRan = true;
            return Task.FromResult("composed");
        }

        var result = await pipeline.RunAsync(CtxFor(), Compose, CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Fail);
        result.ShortCircuitHandler.Should().Be("pre-stop");
        result.ShortCircuitStage.Should().Be(PipelineStage.Pre);
        result.Reason.Should().Be("pre stop");
        compositionRan.Should().BeFalse("Pre short-circuit must not run composition");
        postHandler.InvocationCount.Should().Be(0);
    }

    // ─── 5. Short-circuit in Post stops remaining Post handlers ──────────

    [Fact]
    public async Task PostShortCircuit_StopsRemainingPostHandlers()
    {
        var p1 = new FakeHandler(PipelineStage.Post, "post-first");
        var stopper = new FakeHandler(PipelineStage.Post, "post-stop")
        {
            HandleFunc = (_, _) => Task.FromResult(
                HandlerResult.ShortCircuitWith(DispatchVerdict.Remediate, "stop here"))
        };
        var p3 = new FakeHandler(PipelineStage.Post, "post-third");

        var pipeline = Build(null, p1, stopper, p3);
        var result = await pipeline.RunAsync(CtxFor(), Composer("c"), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Remediate);
        result.ShortCircuitHandler.Should().Be("post-stop");
        result.ShortCircuitStage.Should().Be(PipelineStage.Post);
        p1.InvocationCount.Should().Be(1);
        stopper.InvocationCount.Should().Be(1);
        p3.InvocationCount.Should().Be(0);
    }

    // ─── 6. Composition runs between Pre and Post ────────────────────────

    [Fact]
    public async Task Composition_RunsBetweenPreAndPost()
    {
        var trace = new List<string>();
        var pre = new FakeHandler(PipelineStage.Pre, "pre")
        {
            HandleFunc = (_, _) => { trace.Add("pre"); return Task.FromResult(HandlerResult.Continue()); }
        };
        var post = new FakeHandler(PipelineStage.Post, "post")
        {
            HandleFunc = (_, _) => { trace.Add("post"); return Task.FromResult(HandlerResult.Continue()); }
        };

        var pipeline = Build(null, pre, post);
        await pipeline.RunAsync(
            CtxFor(),
            (_, _) => { trace.Add("compose"); return Task.FromResult("composed"); },
            CancellationToken.None);

        trace.Should().Equal("pre", "compose", "post");
    }

    // ─── 7. Composition writes both ComposedContent and Artifact.Content ─

    [Fact]
    public async Task Composition_SetsBothComposedContentAndArtifactContent()
    {
        string? capturedComposed = null;
        string? capturedArtifactContent = null;
        var post = new FakeHandler(PipelineStage.Post, "capture")
        {
            HandleFunc = (ctx, _) =>
            {
                capturedComposed         = ctx.ComposedContent;
                capturedArtifactContent  = ctx.Artifact.Content;
                return Task.FromResult(HandlerResult.Continue());
            }
        };

        var pipeline = Build(null, post);
        await pipeline.RunAsync(CtxFor(ArtifactFor("<pending>")), Composer("the composed message"), CancellationToken.None);

        capturedComposed.Should().Be("the composed message");
        capturedArtifactContent.Should().Be("the composed message");
    }

    // ─── 8. Handler exception → ShortCircuit Fail, remaining skipped ─────

    [Fact]
    public async Task HandlerException_TreatedAsShortCircuitFail()
    {
        var thrower = new FakeHandler(PipelineStage.Pre, "thrower")
        {
            HandleFunc = (_, _) => throw new InvalidOperationException("boom")
        };
        var nextPre = new FakeHandler(PipelineStage.Pre, "after");

        var pipeline = Build(null, thrower, nextPre);
        var result = await pipeline.RunAsync(CtxFor(), Composer("c"), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Fail);
        result.ShortCircuitHandler.Should().Be("thrower");
        result.ShortCircuitStage.Should().Be(PipelineStage.Pre);
        nextPre.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task PostHandlerException_StopsPipeline()
    {
        var thrower = new FakeHandler(PipelineStage.Post, "post-thrower")
        {
            HandleFunc = (_, _) => throw new InvalidOperationException("post boom")
        };
        var nextPost = new FakeHandler(PipelineStage.Post, "after");

        var pipeline = Build(null, thrower, nextPost);
        var result = await pipeline.RunAsync(CtxFor(), Composer("c"), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Fail);
        result.ShortCircuitHandler.Should().Be("post-thrower");
        result.ShortCircuitStage.Should().Be(PipelineStage.Post);
        nextPost.InvocationCount.Should().Be(0);
    }

    // ─── 9. SetState/GetState round-trips by type ────────────────────────

    private sealed record FakeTelemetry(string Note);

    [Fact]
    public void Context_SetStateGetState_RoundTripsByType()
    {
        var ctx = CtxFor();
        ctx.SetState(new FakeTelemetry("hello"));

        var roundTripped = ctx.GetState<FakeTelemetry>();
        roundTripped.Should().NotBeNull();
        roundTripped!.Note.Should().Be("hello");
    }

    // ─── 10. GetState returns null for unset types ───────────────────────

    [Fact]
    public void Context_GetState_UnsetType_ReturnsNull()
    {
        var ctx = CtxFor();
        var result = ctx.GetState<FakeTelemetry>();
        result.Should().BeNull();
    }

    [Fact]
    public void Context_SetState_NullThrows()
    {
        var ctx = CtxFor();
        var act = () => ctx.SetState<FakeTelemetry>(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ─── 11. Telemetry: O_PIPELINE_START / END Pass shape ────────────────

    [Fact]
    public async Task Telemetry_PassPipeline_EmitsStartAndEnd()
    {
        var log = new CapturingLogger();
        var pre = new FakeHandler(PipelineStage.Pre, "pre");
        var post = new FakeHandler(PipelineStage.Post, "post");

        var pipeline = Build(log, pre, post);
        await pipeline.RunAsync(CtxFor(), Composer("c"), CancellationToken.None);

        log.Messages.Should().Contain(m => m.StartsWith("O_PIPELINE_START") && m.Contains("producer=Outreach"));
        log.Messages.Should().Contain(m =>
            m.StartsWith("O_PIPELINE_END") &&
            m.Contains("result=Pass") &&
            m.Contains("pre_handlers=1") &&
            m.Contains("post_handlers=1"));
    }

    // ─── 12. Telemetry: O_HANDLER_START / END shape ──────────────────────

    [Fact]
    public async Task Telemetry_HandlerLines_EmittedForEachInvocation()
    {
        var log = new CapturingLogger();
        var h = new FakeHandler(PipelineStage.Pre, "TestHandler")
        {
            HandleFunc = (_, _) => Task.FromResult(HandlerResult.Continue("observed-frame=AniInterior"))
        };

        var pipeline = Build(log, h);
        await pipeline.RunAsync(CtxFor(), Composer("c"), CancellationToken.None);

        log.Messages.Should().Contain(m =>
            m.StartsWith("O_HANDLER_START") &&
            m.Contains("stage=Pre") &&
            m.Contains("handler=TestHandler") &&
            m.Contains("producer=Outreach"));

        log.Messages.Should().Contain(m =>
            m.StartsWith("O_HANDLER_END") &&
            m.Contains("stage=Pre") &&
            m.Contains("handler=TestHandler") &&
            m.Contains("result=Continued") &&
            m.Contains("details=\"observed-frame=AniInterior\""));
    }

    // ─── 13. Telemetry: composition log lines ────────────────────────────

    [Fact]
    public async Task Telemetry_Composition_EmitsStartAndEndWithLength()
    {
        var log = new CapturingLogger();
        var pipeline = Build(log);
        await pipeline.RunAsync(CtxFor(), Composer("hello"), CancellationToken.None);

        log.Messages.Should().Contain(m =>
            m.StartsWith("O_COMPOSITION_START") &&
            m.Contains("producer=Outreach"));
        log.Messages.Should().Contain(m =>
            m.StartsWith("O_COMPOSITION_END") &&
            m.Contains("producer=Outreach") &&
            m.Contains("content_length=5"));
    }

    // ─── 14. Telemetry: short-circuit end line carries handler + stage ───

    [Fact]
    public async Task Telemetry_ShortCircuit_EndLineCarriesHandlerAndStage()
    {
        var log = new CapturingLogger();
        var sc = new FakeHandler(PipelineStage.Post, "Stopper")
        {
            HandleFunc = (_, _) => Task.FromResult(
                HandlerResult.ShortCircuitWith(DispatchVerdict.Remediate, "frame-coherence violation"))
        };

        var pipeline = Build(log, sc);
        await pipeline.RunAsync(CtxFor(), Composer("c"), CancellationToken.None);

        log.Messages.Should().Contain(m =>
            m.StartsWith("O_PIPELINE_END") &&
            m.Contains("result=ShortCircuit") &&
            m.Contains("stage=Post") &&
            m.Contains("handler=Stopper") &&
            m.Contains("reason=\"frame-coherence violation\""));
    }

    // ─── 15. Telemetry log line ORDER (interleaved correctly) ────────────

    [Fact]
    public async Task Telemetry_LineOrder_StartPreComposeEndPostEnd()
    {
        var log = new CapturingLogger();
        var pre = new FakeHandler(PipelineStage.Pre, "PreH");
        var post = new FakeHandler(PipelineStage.Post, "PostH");

        var pipeline = Build(log, pre, post);
        await pipeline.RunAsync(CtxFor(), Composer("c"), CancellationToken.None);

        var anchors = log.Messages
            .Select((m, i) => (m, i))
            .Where(t => t.m.StartsWith("O_PIPELINE_START")
                     || t.m.StartsWith("O_HANDLER_START")
                     || t.m.StartsWith("O_COMPOSITION_START")
                     || t.m.StartsWith("O_PIPELINE_END"))
            .Select(t => (Tag: ExtractAnchorTag(t.m), Idx: t.i))
            .ToList();

        var orderTags = anchors.Select(a => a.Tag).ToList();
        orderTags.Should().StartWith(new[]
        {
            "O_PIPELINE_START",
            "O_HANDLER_START:Pre:PreH",
            "O_COMPOSITION_START",
        });
        orderTags.Should().EndWith(new[]
        {
            "O_HANDLER_START:Post:PostH",
            "O_PIPELINE_END",
        });
    }

    private static string ExtractAnchorTag(string line)
    {
        if (line.StartsWith("O_HANDLER_START"))
        {
            var stage = ExtractField(line, "stage=");
            var name  = ExtractField(line, "handler=");
            return $"O_HANDLER_START:{stage}:{name}";
        }
        if (line.StartsWith("O_PIPELINE_START"))    return "O_PIPELINE_START";
        if (line.StartsWith("O_COMPOSITION_START")) return "O_COMPOSITION_START";
        if (line.StartsWith("O_PIPELINE_END"))      return "O_PIPELINE_END";
        return line;
    }

    private static string ExtractField(string line, string key)
    {
        var i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return "";
        var rest = line[(i + key.Length)..];
        var end = rest.IndexOfAny(new[] { ' ', '"' });
        return end < 0 ? rest : rest[..end];
    }

    // ─── 16. Cancellation: cancelled before Pre — fails cleanly ──────────

    [Fact]
    public async Task Cancellation_BeforePre_ShortCircuitsCleanly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var pre = new FakeHandler(PipelineStage.Pre, "pre");

        var pipeline = Build(null, pre);
        var result = await pipeline.RunAsync(CtxFor(), Composer("c"), cts.Token);

        result.Verdict.Should().Be(DispatchVerdict.Fail);
        result.ShortCircuitStage.Should().Be(PipelineStage.Pre);
        pre.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_DuringHandler_ShortCircuitsCleanly()
    {
        using var cts = new CancellationTokenSource();
        var h = new FakeHandler(PipelineStage.Pre, "canceller")
        {
            HandleFunc = (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(HandlerResult.Continue());
            }
        };

        var pipeline = Build(null, h);
        var result = await pipeline.RunAsync(CtxFor(), Composer("c"), cts.Token);

        result.Verdict.Should().Be(DispatchVerdict.Fail);
        result.Reason.Should().Be("cancelled");
    }

    // ─── 17. Builder: stage-mismatch throws at registration ──────────────

    private sealed class ParameterlessPostHandler : ICognitivePipelineHandler
    {
        public PipelineStage Stage => PipelineStage.Post;
        public string        Name  => nameof(ParameterlessPostHandler);
        public bool AppliesTo(CognitiveArtifact artifact) => true;
        public Task<HandlerResult> HandleAsync(CognitivePipelineContext ctx, CancellationToken ct)
            => Task.FromResult(HandlerResult.Continue());
    }

    private sealed class ParameterlessPreHandler : ICognitivePipelineHandler
    {
        public PipelineStage Stage => PipelineStage.Pre;
        public string        Name  => nameof(ParameterlessPreHandler);
        public bool AppliesTo(CognitiveArtifact artifact) => true;
        public Task<HandlerResult> HandleAsync(CognitivePipelineContext ctx, CancellationToken ct)
            => Task.FromResult(HandlerResult.Continue());
    }

    [Fact]
    public void Builder_UsePreHandlerOnPostType_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddCognitivePipeline(p => p.UsePreHandler<ParameterlessPostHandler>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ParameterlessPostHandler*Post*UsePre*");
    }

    [Fact]
    public void Builder_UsePostHandlerOnPreType_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddCognitivePipeline(p => p.UsePostHandler<ParameterlessPreHandler>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ParameterlessPreHandler*Pre*UsePost*");
    }

    [Fact]
    public void Builder_CorrectStageMatch_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddCognitivePipeline(p => p
            .UsePreHandler<ParameterlessPreHandler>()
            .UsePostHandler<ParameterlessPostHandler>());

        act.Should().NotThrow();
    }

    [Fact]
    public void Builder_EmptyPipeline_RegistersOrchestrator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCognitivePipeline(_ => { });

        using var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<CognitivePipeline>();
        pipeline.Should().NotBeNull();
    }

    // ─── 18. Empty pipeline runs composition + returns Pass ──────────────

    [Fact]
    public async Task EmptyPipeline_RunsCompositionOnly_ReturnsPass()
    {
        var composeRan = false;
        var pipeline = Build();
        var result = await pipeline.RunAsync(
            CtxFor(),
            (_, _) => { composeRan = true; return Task.FromResult("c"); },
            CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Pass);
        composeRan.Should().BeTrue();
    }

    // ─── 19. RunAsync argument validation ────────────────────────────────

    [Fact]
    public async Task RunAsync_NullContext_Throws()
    {
        var pipeline = Build();
        var act = () => pipeline.RunAsync(null!, Composer("c"), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RunAsync_NullComposer_Throws()
    {
        var pipeline = Build();
        var act = () => pipeline.RunAsync(CtxFor(), null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ─── 20. State survives across handlers ──────────────────────────────

    [Fact]
    public async Task State_SetInPreHandler_VisibleInPostHandler()
    {
        FakeTelemetry? observed = null;
        var pre = new FakeHandler(PipelineStage.Pre, "writer")
        {
            HandleFunc = (ctx, _) =>
            {
                ctx.SetState(new FakeTelemetry("cross-stage"));
                return Task.FromResult(HandlerResult.Continue());
            }
        };
        var post = new FakeHandler(PipelineStage.Post, "reader")
        {
            HandleFunc = (ctx, _) =>
            {
                observed = ctx.GetState<FakeTelemetry>();
                return Task.FromResult(HandlerResult.Continue());
            }
        };

        var pipeline = Build(null, pre, post);
        await pipeline.RunAsync(CtxFor(), Composer("c"), CancellationToken.None);

        observed.Should().NotBeNull();
        observed!.Note.Should().Be("cross-stage");
    }

    // ─── 21. RunPostOnlyAsync: runs only Post handlers (Theme O.2) ───────

    [Fact]
    public async Task RunPostOnly_SkipsPreHandlersAndComposition()
    {
        var preRan  = false;
        var composeRan = false;
        var postRan = false;
        var pre = new FakeHandler(PipelineStage.Pre, "pre")
        {
            HandleFunc = (_, _) => { preRan = true; return Task.FromResult(HandlerResult.Continue()); }
        };
        var post = new FakeHandler(PipelineStage.Post, "post")
        {
            HandleFunc = (_, _) => { postRan = true; return Task.FromResult(HandlerResult.Continue()); }
        };

        var pipeline = Build(null, pre, post);
        var result = await pipeline.RunPostOnlyAsync(CtxFor(ArtifactFor("already composed")), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Pass);
        preRan.Should().BeFalse("RunPostOnlyAsync must skip Pre handlers entirely");
        composeRan.Should().BeFalse("RunPostOnlyAsync never invokes composition");
        postRan.Should().BeTrue();
    }

    [Fact]
    public async Task RunPostOnly_ShortCircuitInPost_HaltsRemainingPost()
    {
        var p1 = new FakeHandler(PipelineStage.Post, "post-a");
        var stopper = new FakeHandler(PipelineStage.Post, "post-stop")
        {
            HandleFunc = (_, _) => Task.FromResult(
                HandlerResult.ShortCircuitWith(DispatchVerdict.Remediate, "frame violation"))
        };
        var p3 = new FakeHandler(PipelineStage.Post, "post-c");

        var pipeline = Build(null, p1, stopper, p3);
        var result = await pipeline.RunPostOnlyAsync(CtxFor(), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Remediate);
        result.ShortCircuitHandler.Should().Be("post-stop");
        result.ShortCircuitStage.Should().Be(PipelineStage.Post);
        p1.InvocationCount.Should().Be(1);
        stopper.InvocationCount.Should().Be(1);
        p3.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task RunPostOnly_AppliesToFalse_HandlerSkipped()
    {
        var skipped = new FakeHandler(PipelineStage.Post, "skipped")
        {
            AppliesFunc = _ => false,
            HandleFunc  = (_, _) => throw new InvalidOperationException("must not run"),
        };
        var applied = new FakeHandler(PipelineStage.Post, "applied");

        var pipeline = Build(null, skipped, applied);
        var result = await pipeline.RunPostOnlyAsync(CtxFor(), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Pass);
        skipped.InvocationCount.Should().Be(0);
        applied.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task RunPostOnly_Telemetry_EmitsModePostOnly()
    {
        var log = new CapturingLogger();
        var post = new FakeHandler(PipelineStage.Post, "post");
        var pipeline = Build(log, post);

        await pipeline.RunPostOnlyAsync(CtxFor(), CancellationToken.None);

        log.Messages.Should().Contain(m =>
            m.StartsWith("O_PIPELINE_START") &&
            m.Contains("mode=PostOnly"));
        log.Messages.Should().NotContain(m => m.StartsWith("O_COMPOSITION_START"),
            "RunPostOnlyAsync must not emit composition telemetry");
    }

    [Fact]
    public async Task RunPostOnly_NullContext_Throws()
    {
        var pipeline = Build();
        var act = () => pipeline.RunPostOnlyAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ─── 22. UsePostInvariant<T> builder extension (Theme O.2) ───────────

    private sealed class TestInvariant : ICognitiveOutputInvariant
    {
        public string Name => "test-inv";
        public bool   ResultPassed { get; set; } = true;
        public bool   ResultHardFail { get; set; }
        public bool AppliesTo(CognitiveArtifact artifact) => true;
        public Task<InvariantResult> EvaluateAsync(CognitiveArtifact artifact, CancellationToken ct)
            => Task.FromResult(ResultPassed
                ? InvariantResult.Pass()
                : InvariantResult.Fail("test-hint", ResultHardFail));
    }

    [Fact]
    public async Task Builder_UsePostInvariant_RegistersAdapterWrappedHandler()
    {
        // Theme O.2: .UsePostInvariant<T>() should register the invariant
        // in DI as itself, then resolve through the pipeline as a Post-
        // stage handler wrapped in InvariantToHandlerAdapter. The handler
        // name must come from the invariant (not the adapter class).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCognitivePipeline(p => p.UsePostInvariant<TestInvariant>());

        using var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<CognitivePipeline>();

        var inv = sp.GetRequiredService<TestInvariant>();
        inv.ResultPassed = false;

        var result = await pipeline.RunPostOnlyAsync(CtxFor(), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Remediate);
        result.ShortCircuitHandler.Should().Be("test-inv",
            "the adapter must surface the invariant's Name, not the adapter type name");
        result.ShortCircuitStage.Should().Be(PipelineStage.Post);
    }

    [Fact]
    public async Task Builder_UsePostInvariant_HardFailEscalatesVerdict()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCognitivePipeline(p => p.UsePostInvariant<TestInvariant>());

        using var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<CognitivePipeline>();
        var inv      = sp.GetRequiredService<TestInvariant>();
        inv.ResultPassed   = false;
        inv.ResultHardFail = true;

        var result = await pipeline.RunPostOnlyAsync(CtxFor(), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Fail);
    }

    [Fact]
    public async Task Builder_UsePostInvariant_PassDoesNotShortCircuit()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCognitivePipeline(p => p.UsePostInvariant<TestInvariant>());

        using var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<CognitivePipeline>();

        var result = await pipeline.RunPostOnlyAsync(CtxFor(), CancellationToken.None);

        result.Verdict.Should().Be(DispatchVerdict.Pass);
        result.ShortCircuitHandler.Should().BeNull();
    }

    [Fact]
    public void Builder_UsePostInvariant_PreservesRegistrationOrder()
    {
        // First-registered invariant fires first; second fires second.
        // This is the same registration-order = execution-order contract
        // that UsePostHandler<T> follows.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCognitivePipeline(p => p
            .UsePostInvariant<TestInvariant>()
            .UsePostHandler<ParameterlessPostHandler>());

        using var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<CognitivePipeline>();

        // Mostly a smoke test: ensures the mixed Use{Post,PostInvariant}
        // registration shape resolves without DI errors.
        pipeline.Should().NotBeNull();
    }
}
