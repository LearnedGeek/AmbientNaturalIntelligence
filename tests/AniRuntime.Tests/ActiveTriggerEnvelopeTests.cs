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
        var vecB = NearDuplicateVector(vecA, seed: 2);  // cosine >0.99 with vecA
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
    public async Task AddTriggerAsync_NearDuplicateAcrossDifferentTriggerType_DoesNotMerge()
    {
        // Mark review 2026-08-15 [P1]: cross-type dedup would destroy provenance.
        // Same embedding-similar text arriving as SpontaneousThought then as
        // ReactiveShare must NOT collapse into one envelope tagged with the
        // earlier type.
        DesireState? saved = null;
        SetupSavesToDelegate(s => saved = s, FreshDesireState());

        var ollama = new Mock<IOllamaClient>();
        var vecA = MakeUnitVector(seed: 1);
        var vecB = NearDuplicateVector(vecA, seed: 2);  // cosine >0.99 with vecA
        ollama.SetupSequence(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(vecA)
              .ReturnsAsync(vecB);

        var engine = EngineWithOllama(ollama.Object);

        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.5f, "reflection on the news");
        await engine.AddTriggerAsync(TriggerType.ReactiveShare,      0.5f, "reflection on the news");

        saved!.ActiveTriggers.Should().HaveCount(2, "different trigger types must not merge even at cosine ≈1.0");
        saved.ActiveTriggers.Select(t => t.Type)
            .Should().BeEquivalentTo(new[] { TriggerType.SpontaneousThought, TriggerType.ReactiveShare });
    }

    [Fact]
    public async Task AddTriggerAsync_NearDuplicateAcrossDifferentSource_DoesNotMerge()
    {
        // Mark review 2026-08-15 [P1]: same TriggerType from different call
        // sites (source discriminator) must not collapse. Both call sites
        // currently use SpontaneousThought; only Source distinguishes them.
        DesireState? saved = null;
        SetupSavesToDelegate(s => saved = s, FreshDesireState());

        var ollama = new Mock<IOllamaClient>();
        var vecA = MakeUnitVector(seed: 1);
        var vecB = NearDuplicateVector(vecA, seed: 2);
        ollama.SetupSequence(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(vecA)
              .ReturnsAsync(vecB);

        var engine = EngineWithOllama(ollama.Object);

        await engine.AddTriggerAsync(
            TriggerType.SpontaneousThought, 0.5f, "thinking about Mark", default,
            source: "inner-thought-valence");
        await engine.AddTriggerAsync(
            TriggerType.SpontaneousThought, 0.5f, "thinking about Mark", default,
            source: "held-back-outreach");

        saved!.ActiveTriggers.Should().HaveCount(2, "different sources on same TriggerType must not merge");
        saved.ActiveTriggers.Select(t => t.Source)
            .Should().BeEquivalentTo(new[] { "inner-thought-valence", "held-back-outreach" });

        // Rendering must reflect the distinct source tags.
        IActiveTriggerEnvelope innerThought = saved.ActiveTriggers.First(t => t.Source == "inner-thought-valence");
        IActiveTriggerEnvelope heldBack     = saved.ActiveTriggers.First(t => t.Source == "held-back-outreach");
        innerThought.SourceType.Should().Be("trigger.inner-thought-valence");
        heldBack.SourceType.Should().Be("trigger.held-back-outreach");
    }

    [Fact]
    public async Task AddTriggerAsync_Merge_KeepsOriginalSemanticKeyAndDescription()
    {
        // Mark review 2026-08-15 [P2]: on merge, SemanticKey must stay paired
        // with the ORIGINAL Description. If we reassigned key to the new
        // embedding while leaving Description unchanged, chained
        // near-threshold merges could walk the key away from the displayed
        // content and start collapsing dissimilar-to-displayed triggers.
        DesireState? saved = null;
        SetupSavesToDelegate(s => saved = s, FreshDesireState());

        var ollama = new Mock<IOllamaClient>();
        var vecA = MakeUnitVector(seed: 1);
        var vecB = NearDuplicateVector(vecA, seed: 2);
        var vecC = NearDuplicateVector(vecB, seed: 3);
        ollama.SetupSequence(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(vecA)
              .ReturnsAsync(vecB)
              .ReturnsAsync(vecC);

        var engine = EngineWithOllama(ollama.Object);

        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.4f, "original description");
        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.5f, "second phrasing");
        await engine.AddTriggerAsync(TriggerType.SpontaneousThought, 0.6f, "third phrasing");

        saved!.ActiveTriggers.Should().HaveCount(1, "all three should merge into the first envelope");
        saved.ActiveTriggers[0].Description.Should().Be("original description",
            "merge must not overwrite the displayed Description");
        saved.ActiveTriggers[0].SemanticKey.Should().BeSameAs(vecA,
            "merge must not overwrite SemanticKey — key stays paired with Description");
        saved.ActiveTriggers[0].Weight.Should().Be(0.6f, "weight is the max across all merges");
    }

    [Fact]
    public async Task AddTriggerAsync_Cancellation_PropagatesToCaller()
    {
        // Serge review 2026-08-15 IMPORTANT: broad catch (Exception) around
        // EmbedAsync must NOT swallow OperationCanceledException. Cancellation
        // has to propagate so service shutdown / request timeout surfaces
        // instead of silently falling through to the append path.
        DesireState? saved = null;
        SetupSavesToDelegate(s => saved = s, FreshDesireState());

        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new OperationCanceledException("simulated cancellation"));

        var engine = EngineWithOllama(ollama.Object);

        var act = () => engine.AddTriggerAsync(
            TriggerType.SpontaneousThought, 0.5f, "will cancel",
            new CancellationTokenSource().Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
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
        user.Should().NotContain("trigger-3").And.NotContain("trigger-4")
            .And.NotContain("trigger-5").And.NotContain("trigger-6").And.NotContain("trigger-7");
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

    /// <summary>
    /// Produces a unit vector very close to <paramref name="v"/> (cosine
    /// similarity typically ≥0.99) by adding tiny per-component noise and
    /// re-normalising. Intended for exercising the ≥0.85 dedup threshold —
    /// safely well above it. NOT a precision helper: the exact similarity
    /// depends on the RNG seed and vector dimension; assert on merge
    /// outcome rather than on the raw similarity value.
    /// </summary>
    private static float[] NearDuplicateVector(float[] v, int seed, int dim = 32)
    {
        var rand = new Random(seed);
        var w = new float[v.Length];
        for (var i = 0; i < v.Length; i++)
            w[i] = v[i] + (float)((rand.NextDouble() * 2 - 1) * 0.01);
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
