using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 2 tests — <see cref="IActiveTriggerEnvelope"/>
/// contract, <see cref="DesireEngine.AddTriggerAsync"/> semantic-dedup / cap
/// behaviour, and <see cref="AniRuntime.LLM.Prompts.OutreachPromptCommand"/>
/// type-tagged rendering.
///
/// Acceptance criterion for the phase (per ANI-Foundation-Input-Refactor-Plan.md):
/// ActiveTriggers rendered ≤10 entries, ≥6 semantically-distinct.
/// </summary>
public class ActiveTriggerEnvelopeTests : AniTestBase
{
    // ── Envelope contract on DesireTrigger ──────────────────────────────────

    [Fact]
    public void DesireTrigger_ImplementsActiveTriggerEnvelope_ExposesContentAsDescription()
    {
        var trig = new DesireTrigger
        {
            Type        = TriggerType.SpontaneousThought,
            Weight      = 0.7f,
            Description = "thought of Mark",
        };
        IActiveTriggerEnvelope env = trig;

        env.Content.Should().Be("thought of Mark");
        env.TriggerType.Should().Be(TriggerType.SpontaneousThought);
        env.Weight.Should().Be(0.7f);
        env.Producer.Should().Be("DesireEngine");
        env.SourceType.Should().Be("trigger.spontaneous-thought");
    }

    [Theory]
    [InlineData(TriggerType.TemporalDrift,      "trigger.temporal-drift")]
    [InlineData(TriggerType.OpenLoop,           "trigger.open-loop")]
    [InlineData(TriggerType.AssociativeFire,    "trigger.associative-fire")]
    [InlineData(TriggerType.EmotionalResidue,   "trigger.emotional-residue")]
    [InlineData(TriggerType.SpontaneousThought, "trigger.spontaneous-thought")]
    [InlineData(TriggerType.ContextualMoment,   "trigger.contextual-moment")]
    [InlineData(TriggerType.IntegrationEvent,   "trigger.integration-event")]
    [InlineData(TriggerType.ReactiveShare,      "trigger.reactive-share")]
    public void DesireTrigger_SourceType_MapsFromTriggerType(TriggerType type, string expected)
    {
        IActiveTriggerEnvelope env = new DesireTrigger { Type = type };
        env.SourceType.Should().Be(expected);
    }

    // ── Semantic dedup at AddTriggerAsync ──────────────────────────────────

    private DesireEngine EngineWithOllama(IOllamaClient ollama, AniOptions? opts = null)
    {
        var options = Options.Create(opts ?? new AniOptions
        {
            TriggerSemanticDedupEnabled   = true,
            TriggerSemanticDedupThreshold = 0.85,
            TriggerMaxActive              = 15,
            TriggerDesireMultiplier       = 0.15,
        });
        return new DesireEngine(
            MockMemory.Object, MockMemory.Object, options,
            NullLogger<DesireEngine>.Instance, ollama);
    }

    private void SetupSavesToDelegate(Action<DesireState> onSave, DesireState initial)
    {
        var state = initial;
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => state);
        MockMemory.Setup(m => m.SaveDesireStateAsync(It.IsAny<DesireState>(), It.IsAny<CancellationToken>()))
                  .Callback<DesireState, CancellationToken>((s, _) => { state = s; onSave(s); })
                  .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task AddTriggerAsync_NearDuplicateEmbedding_MergesInsteadOfAppending()
    {
        DesireState? saved = null;
        SetupSavesToDelegate(s => saved = s, FreshDesireState() with { DesireToConnect = 0.2f });

        var ollama = new Mock<IOllamaClient>();
        var vecA = MakeUnitVector(seed: 1);
        var vecB = ScaleVector(vecA, 0.98f); // ~0.98 cosine similarity → dupe
        ollama.SetupSequence(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(vecA)
              .ReturnsAsync(vecB);

        var engine = EngineWithOllama(ollama.Object);

        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.5f, "thought about Mark's teaching");
        var desireAfterFirst = saved!.DesireToConnect;

        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.9f, "thinking about Mark's students");

        saved.ActiveTriggers.Should().HaveCount(1, "near-duplicate should merge into the existing envelope");
        saved.ActiveTriggers[0].Weight.Should().Be(0.9f, "merge should take the max of both weights");
        saved.DesireToConnect.Should().Be(desireAfterFirst, "duplicate should NOT bump desire again");
    }

    [Fact]
    public async Task AddTriggerAsync_DissimilarEmbedding_AppendsNewEnvelope()
    {
        DesireState? saved = null;
        SetupSavesToDelegate(s => saved = s, FreshDesireState() with { DesireToConnect = 0.2f });

        var ollama = new Mock<IOllamaClient>();
        var vecA = MakeUnitVector(seed: 1);
        var vecB = MakeUnitVector(seed: 999);  // orthogonal-ish → below threshold
        ollama.SetupSequence(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(vecA)
              .ReturnsAsync(vecB);

        var engine = EngineWithOllama(ollama.Object);

        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.5f, "thought about Mark's teaching");
        var desireAfterFirst = saved!.DesireToConnect;

        await engine.AddTriggerAsync(TriggerType.ReactiveShare, 0.4f, "news article about local news");

        saved.ActiveTriggers.Should().HaveCount(2, "dissimilar triggers should stack");
        saved.DesireToConnect.Should().BeGreaterThan(desireAfterFirst, "distinct trigger DOES bump desire");
    }

    [Fact]
    public async Task AddTriggerAsync_EmbeddingFailure_DegradesToAppendOnlyPath()
    {
        DesireState? saved = null;
        SetupSavesToDelegate(s => saved = s, FreshDesireState());

        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new HttpRequestException("Ollama unreachable"));

        var engine = EngineWithOllama(ollama.Object);

        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.5f, "one");
        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.5f, "two");

        saved!.ActiveTriggers.Should().HaveCount(2, "embedding failure must fall back to append behaviour");
        saved.ActiveTriggers.Should().OnlyContain(t => t.SemanticKey == null);
    }

    [Fact]
    public async Task AddTriggerAsync_NullOllama_DegradesToAppendOnlyPath()
    {
        DesireState? saved = null;
        SetupSavesToDelegate(s => saved = s, FreshDesireState());

        var engine = new DesireEngine(MockMemory.Object, MockMemory.Object, DefaultOptions,
            NullLogger<DesireEngine>.Instance, ollama: null);

        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.5f, "one");
        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.5f, "two");

        saved!.ActiveTriggers.Should().HaveCount(2, "null Ollama is legal — no dedup, plain append");
    }

    [Fact]
    public async Task AddTriggerAsync_ExceedingCap_DropsOldestFirst()
    {
        var opts = new AniOptions
        {
            TriggerSemanticDedupEnabled = false,  // isolate cap behaviour from dedup
            TriggerMaxActive            = 3,
            TriggerDesireMultiplier     = 0.15,
        };
        DesireState? saved = null;
        SetupSavesToDelegate(s => saved = s, FreshDesireState());

        var engine = EngineWithOllama(new Mock<IOllamaClient>().Object, opts);

        for (var i = 0; i < 5; i++)
            await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.3f, $"trigger-{i}");

        saved!.ActiveTriggers.Should().HaveCount(3);
        saved.ActiveTriggers.Select(t => t.Description)
            .Should().BeEquivalentTo(new[] { "trigger-2", "trigger-3", "trigger-4" });
    }

    [Fact]
    public async Task AddTriggerAsync_DedupDisabled_AppendsRegardlessOfSimilarity()
    {
        var opts = new AniOptions
        {
            TriggerSemanticDedupEnabled = false,
            TriggerMaxActive            = 15,
            TriggerDesireMultiplier     = 0.15,
        };
        DesireState? saved = null;
        SetupSavesToDelegate(s => saved = s, FreshDesireState());

        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);  // must not be called
        var engine = EngineWithOllama(ollama.Object, opts);

        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.5f, "one");
        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.5f, "one");

        saved!.ActiveTriggers.Should().HaveCount(2);
        ollama.Verify(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Rendering — type-tag + top-K ────────────────────────────────────────

    [Fact]
    public void OutreachPrompt_RendersActiveTriggers_WithTypeTag()
    {
        var snapshot = MinimalContextSnapshot();
        snapshot.DesireState.ActiveTriggers.AddRange(new[]
        {
            new DesireTrigger { Type = TriggerType.SpontaneousThought, Description = "thought of Mark",     CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) },
            new DesireTrigger { Type = TriggerType.ReactiveShare,      Description = "news about baseball", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2) },
        });

        var (_, user) = PromptBuilder.BuildOutreachPrompt(snapshot, "some thought");

        user.Should().Contain("Active triggers:");
        user.Should().Contain("[trigger.spontaneous-thought] thought of Mark");
        user.Should().Contain("[trigger.reactive-share] news about baseball");
    }

    [Fact]
    public void OutreachPrompt_CapsAtTopK_MostRecentFirst()
    {
        var snapshot = MinimalContextSnapshot();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 8; i++)
        {
            snapshot.DesireState.ActiveTriggers.Add(new DesireTrigger
            {
                Type        = TriggerType.SpontaneousThought,
                Description = $"trigger-{i}",
                CreatedAt   = now.AddMinutes(-i),
            });
        }

        var (_, user) = PromptBuilder.BuildOutreachPrompt(
            snapshot, "some thought", triggerRenderTopK: 3);

        // Top-3 by CreatedAt DESC: trigger-0, trigger-1, trigger-2.
        user.Should().Contain("trigger-0").And.Contain("trigger-1").And.Contain("trigger-2");
        user.Should().NotContain("trigger-4").And.NotContain("trigger-5")
            .And.NotContain("trigger-6").And.NotContain("trigger-7");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static float[] MakeUnitVector(int seed, int dim = 32)
    {
        var rand = new Random(seed);
        var v = new float[dim];
        double mag = 0;
        for (var i = 0; i < dim; i++) { v[i] = (float)(rand.NextDouble() * 2 - 1); mag += v[i] * v[i]; }
        var normalizer = 1.0 / Math.Sqrt(mag);
        for (var i = 0; i < dim; i++) v[i] = (float)(v[i] * normalizer);
        return v;
    }

    private static float[] ScaleVector(float[] v, float factor)
    {
        // Perturb v slightly then re-normalize — produces a near-duplicate vector
        // whose cosine similarity with v is ~factor.
        var rand = new Random(42);
        var w = new float[v.Length];
        for (var i = 0; i < v.Length; i++)
            w[i] = v[i] * factor + (float)((rand.NextDouble() * 2 - 1) * (1 - factor) * 0.1);
        // Normalize
        double mag = 0;
        foreach (var x in w) mag += x * x;
        var norm = 1.0 / Math.Sqrt(mag);
        for (var i = 0; i < w.Length; i++) w[i] = (float)(w[i] * norm);
        return w;
    }

    private static ContextSnapshot MinimalContextSnapshot() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
        },
        DesireState = new DesireState(),
        RecentHistory = new List<ChatMessage>(),
        OpenLoops = new List<OpenLoop>(),
    };
}
