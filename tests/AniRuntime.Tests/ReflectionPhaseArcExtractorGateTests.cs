using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 7 gate hoist (PR #192 / issue #190) — regression test that
/// pins the structural invariant: when <c>GrowthEdgesEnabled=false</c> but
/// <c>OngoingArcsEnabled=true</c>, the arc extractor still runs and its
/// mutation still reaches <c>SaveCharacterStateAsync</c>. The existing
/// <see cref="OngoingArcsMutationTests"/> cover the mutator directly and
/// bypass the gate; the <c>CognitiveCycle*</c> fixtures omit the arc
/// extractor so they never exercised this path either. Without this test,
/// re-nesting Phase 7 under the growth-edges gate would pass CI.
///
/// <para>Copilot flagged this coverage gap on PR #192 review.</para>
/// </summary>
public class ReflectionPhaseArcExtractorGateTests
{
    private const string ReflectionSummaryJson = """
        {"summaries":[{"topic":"the shelf","shape":"almost done"}]}
        """;

    private static IReadOnlyList<MemoryRecord> ThreeThoughts() => new[]
    {
        new MemoryRecord
        {
            Id = Guid.NewGuid(), Content = "building the shelf tonight",
            Type = MemoryType.InnerThought, OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        },
        new MemoryRecord
        {
            Id = Guid.NewGuid(), Content = "turning the brace into something sturdier",
            Type = MemoryType.InnerThought, OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        },
        new MemoryRecord
        {
            Id = Guid.NewGuid(), Content = "shelf lines looking even",
            Type = MemoryType.InnerThought, OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-15),
        },
    };

    private static (ReflectionPhase phase,
                    Mock<IMemoryPersistence> persist,
                    Mock<IArcExtractor> arcExtractor)
        BuildPhase(
            AniOptions options,
            IReadOnlyList<ArcMention> arcMentions)
    {
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
              .ReturnsAsync(ReflectionSummaryJson);

        var persist = new Mock<IMemoryPersistence>();
        persist.Setup(p => p.GetRecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(ThreeThoughts());
        persist.Setup(p => p.GetDecayEligibleAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(ThreeThoughts());
        persist.Setup(p => p.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        persist.Setup(p => p.MarkCompressedAsync(
                It.IsAny<IEnumerable<Guid>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        persist.Setup(p => p.SaveCharacterStateAsync(
                It.IsAny<CharacterStateDoc>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        var search = new Mock<IMemorySearch>();
        search.Setup(s => s.GetByTypeAsync(
                It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<MemoryRecord>());

        var arcExtractor = new Mock<IArcExtractor>();
        arcExtractor.Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<MemoryRecord>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(arcMentions);

        var phase = new ReflectionPhase(
            ollama.Object,
            persist.Object,
            search.Object,
            Options.Create(options),
            NullLogger<ReflectionPhase>.Instance,
            outputGate:    null,
            gistService:   null,              // legacy save path
            growthEdges:   null,              // deliberately absent — the Devin+Copilot ask
            analytics:     null,
            arcExtractor:  arcExtractor.Object,
            personTropeExtractor: null);

        return (phase, persist, arcExtractor);
    }

    private static AniOptions BaseOptions() => new()
    {
        ReflectionEnabled              = true,
        CompressionEnabled             = false, // use GetRecentAsync path
        UseEfReflectionGistService     = false, // legacy save path (no IReflectionGistService needed)
        ReflectionOutputGateEnabled    = false,
    };

    [Fact]
    public async Task ArcExtraction_RunsWhenGrowthEdgesDisabled()
    {
        // The core regression: PR #192 hoisted the arc extraction out of
        // the GrowthEdgesEnabled gate. If someone re-nests it, this test
        // fails — arc extraction wouldn't run and KnownArcs would stay
        // empty despite OngoingArcsEnabled=true.
        var options = BaseOptions();
        options.GrowthEdgesEnabled = false; // the explicit configuration under test
        options.OngoingArcsEnabled = true;

        var mentions = new[] { new ArcMention("the shelf", "almost done", null, false) };
        var (phase, persist, arcExtractor) = BuildPhase(options, mentions);

        var characterState = new CharacterStateDoc { PrimaryContactName = "Mark" };
        var ok = await phase.ForceRunOnceAsync(characterState, CancellationToken.None);

        ok.Should().BeTrue("reflection synthesis produced a saved gist");
        arcExtractor.Verify(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<MemoryRecord>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "arc extraction must run even when growth-edges are disabled (PR #192)");

        characterState.OngoingArcs.Should().ContainSingle();
        characterState.OngoingArcs[0].Name.Should().Be("the shelf");

        persist.Verify(p => p.SaveCharacterStateAsync(
                It.Is<CharacterStateDoc>(cs => cs.OngoingArcs.Count == 1),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "arc mutation must reach the persist step via the unified change-flag");
    }

    [Fact]
    public async Task ArcExtraction_SkippedWhenOngoingArcsDisabled()
    {
        // Flag-off case: own-flag discipline — Phase 7 doesn't run even
        // though growth-edges are on. Guards against accidentally dropping
        // the OngoingArcsEnabled check during the hoist refactor.
        var options = BaseOptions();
        options.GrowthEdgesEnabled = true;
        options.OngoingArcsEnabled = false;

        var mentions = new[] { new ArcMention("the shelf", "almost done", null, false) };
        var (phase, _, arcExtractor) = BuildPhase(options, mentions);

        var characterState = new CharacterStateDoc { PrimaryContactName = "Mark" };
        await phase.ForceRunOnceAsync(characterState, CancellationToken.None);

        arcExtractor.Verify(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<MemoryRecord>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "OngoingArcsEnabled=false must skip arc extraction");

        characterState.OngoingArcs.Should().BeEmpty();
    }
}
