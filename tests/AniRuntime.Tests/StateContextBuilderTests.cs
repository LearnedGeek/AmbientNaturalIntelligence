using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using AniRuntime.Loops.Context;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Coverage for <see cref="StateContextBuilder"/>, the fifth and final
/// sub-builder of the ContextBuilder SRP decomposition (extracted 2026-05-19
/// per `ANI-Testability-Architecture-Plan.md` §2). Tests the state pulls,
/// emotional-state override pattern, Feature 27 outreach-continuity derivation
/// (BuildOutreachContext), and Feature 41 thought-diversity nudge.
/// </summary>
public class StateContextBuilderTests : AniTestBase
{
    private StateContextBuilder Build(IDiagnosticService? diagnostic = null)
    {
        MockMemory.Setup(m => m.GetEmotionalStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new EmotionalState { Warmth = 0.5f });
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(FreshDesireState());

        var diag = diagnostic ?? new Mock<IDiagnosticService>(MockBehavior.Strict).Object;
        if (diagnostic is null)
        {
            // Default — return null report so the nudge degrades.
            var defaultDiag = new Mock<IDiagnosticService>();
            defaultDiag.SetupGet(d => d.LatestReport).Returns((DiagnosticReport?)null);
            diag = defaultDiag.Object;
        }

        var desire = new DesireEngine(
            MockMemory.Object, MockMemory.Object, DefaultOptions,
            NullLogger<DesireEngine>.Instance);

        return new StateContextBuilder(
            MockMemory.Object, MockMemory.Object, desire, diag,
            NullLogger<StateContextBuilder>.Instance);
    }

    [Fact]
    public async Task BuildAsync_PullsCharacterDesireOpenLoopsAndEmotional()
    {
        MockMemory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        MockMemory.Setup(m => m.GetOpenLoopsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<OpenLoop> { new() { Description = "spanish lessons unresolved" } });

        var result = await Build().BuildAsync(
            Array.Empty<MemoryRecord>(), emotionalStateOverride: null, CancellationToken.None);

        result.CharacterState.Name.Should().Be("Ani");
        result.DesireState.Should().NotBeNull();
        result.EmotionalState.Warmth.Should().Be(0.5f);
        result.OpenLoops.Should().ContainSingle(l => l.Description.Contains("spanish"));
    }

    [Fact]
    public async Task BuildAsync_EmotionalStateOverride_TakesPrecedenceOverStore()
    {
        MockMemory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new CharacterStateDoc());
        MockMemory.Setup(m => m.GetOpenLoopsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<OpenLoop>());

        var post = new EmotionalState { Warmth = 0.99f };

        var result = await Build().BuildAsync(
            Array.Empty<MemoryRecord>(), emotionalStateOverride: post, CancellationToken.None);

        result.EmotionalState.Should().BeSameAs(post,
            "override skips the store load — matches pre-extraction orchestrator behavior");
        MockMemory.Verify(m => m.GetEmotionalStateAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BuildAsync_ThoughtDiversityNudge_NullWhenNoDiagnosticReport()
    {
        MockMemory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new CharacterStateDoc());
        MockMemory.Setup(m => m.GetOpenLoopsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<OpenLoop>());

        var result = await Build().BuildAsync(
            Array.Empty<MemoryRecord>(), null, CancellationToken.None);

        result.ThoughtDiversityNudge.Should().BeNull();
    }

    [Fact]
    public async Task BuildAsync_ThoughtDiversityNudge_SurfacedWhenPerceptionAnchorFinding()
    {
        var report = new DiagnosticReport
        {
            Findings = new List<DiagnosticFinding>
            {
                new() { Code = "PERCEPTION-ANCHOR", Description = "stuck on a thought", Severity = DiagnosticSeverity.Info },
            },
        };
        var diag = new Mock<IDiagnosticService>();
        diag.SetupGet(d => d.LatestReport).Returns(report);

        MockMemory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new CharacterStateDoc());
        MockMemory.Setup(m => m.GetOpenLoopsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<OpenLoop>());

        var result = await Build(diag.Object).BuildAsync(
            Array.Empty<MemoryRecord>(), null, CancellationToken.None);

        result.ThoughtDiversityNudge.Should().NotBeNull();
        result.ThoughtDiversityNudge.Should().Contain("circling the same thought");
    }

    [Fact]
    public async Task BuildAsync_OutreachContext_DerivedFromRecentMemory()
    {
        MockMemory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        MockMemory.Setup(m => m.GetOpenLoopsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<OpenLoop>());

        var builder = Build();

        // Override the default FreshDesireState AFTER Build() so our setup wins
        // (Moq last-setup-wins; Build() also sets up GetDesireStateAsync as a
        // default for the simpler test cases above).
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new DesireState
                  {
                      LastContactInbound = DateTimeOffset.UtcNow.AddHours(-2),
                      LastInnerThought   = DateTimeOffset.UtcNow,
                  });

        var t1 = DateTimeOffset.UtcNow.AddMinutes(-30);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-15);
        var recent = new List<MemoryRecord>
        {
            new() { Type = MemoryType.Episodic, OccurredAt = t1,
                    Content = "I reached out to Mark: \"thinking of you\"" },
            new() { Type = MemoryType.Episodic, OccurredAt = t2,
                    Content = "I reached out to Mark: \"still here\"" },
        };

        var result = await builder.BuildAsync(recent, null, CancellationToken.None);

        result.OutreachContext.RecentMessages.Should().HaveCount(2);
        result.OutreachContext.RecentMessages[0].Message.Should().Be("still here",
            "ordered most recent first");
        result.OutreachContext.UnansweredCount.Should().Be(2,
            "lastContactInbound is older than both outreaches, so neither is answered");
    }
}
