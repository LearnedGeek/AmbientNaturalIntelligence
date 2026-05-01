using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.5d (May 1, 2026) — pin the contract for the
/// reflection→gate context-extraction helper. Full ReflectionPhase
/// roundtrip is covered by integration; this test isolates the
/// pure-function helper that builds the gate's contact + ani message
/// context from the recent-memory pool.
/// </summary>
public class ReflectionPhaseGateTests
{
    private static MemoryRecord Mem(string content)
        => new() { Content = content };

    [Fact]
    public void BuildGateContext_SplitsContactAndAniMessages_ByCanonicalPrefix()
    {
        var memories = new List<MemoryRecord>
        {
            Mem("Mark texted: \"hey, what's for dinner?\""),
            Mem("I said to Mark: \"i'm thinking pasta\""),
            Mem("Mark said: \"sounds great\""),
            Mem("I reached out to Mark: \"hey hon, just thinking about you\""),
            Mem("the gap between hours feels real"),                  // semantic — neither
            Mem("Conversation (3 messages): ..."),                    // legacy summary — neither
        };

        var (contactMessages, aniMessages) =
            ReflectionPhase.BuildGateContext(memories, contact: "Mark");

        contactMessages.Should().HaveCount(2);
        contactMessages.Should().Contain(m => m.Contains("what's for dinner"));
        contactMessages.Should().Contain(m => m.Contains("sounds great"));

        aniMessages.Should().HaveCount(2);
        aniMessages.Should().Contain(m => m.Contains("i'm thinking pasta"));
        aniMessages.Should().Contain(m => m.Contains("just thinking about you"));
    }

    [Fact]
    public void BuildGateContext_HandlesContactNameVariation()
    {
        // Records may use either the configured contact name or the literal
        // "Mark" prefix (legacy + cross-instance consistency). Both pass
        // through.
        var memories = new List<MemoryRecord>
        {
            Mem("Sarah texted: \"hey there\""),
            Mem("Mark said: \"hello back\""),
        };

        var (contactMessages, _) = ReflectionPhase.BuildGateContext(memories, contact: "Sarah");
        contactMessages.Should().HaveCount(2,
            "both the configured-contact prefix AND the literal 'Mark' prefix should match — " +
            "the substrate is heterogeneous across migrations");
    }

    [Fact]
    public void BuildGateContext_CapsAtEightMessagesEach()
    {
        var memories = Enumerable.Range(0, 20)
            .SelectMany(i => new[]
            {
                Mem($"Mark texted: \"contact message {i}\""),
                Mem($"I said to Mark: \"ani message {i}\""),
            })
            .ToList();

        var (contactMessages, aniMessages) = ReflectionPhase.BuildGateContext(memories, "Mark");

        contactMessages.Should().HaveCount(8, "cap bounds prompt size for the classifier");
        aniMessages.Should().HaveCount(8);
    }

    [Fact]
    public void BuildGateContext_EmptyInput_ReturnsEmptyLists()
    {
        var (contactMessages, aniMessages) =
            ReflectionPhase.BuildGateContext(Array.Empty<MemoryRecord>(), "Mark");
        contactMessages.Should().BeEmpty();
        aniMessages.Should().BeEmpty();
    }

    [Fact]
    public void BuildGateContext_IgnoresWhitespaceAndEmptyContent()
    {
        var memories = new List<MemoryRecord>
        {
            new() { Content = "" },
            new() { Content = "   " },
            new() { Content = null! },
            Mem("Mark texted: \"real one\""),
        };

        var (contactMessages, _) = ReflectionPhase.BuildGateContext(memories, "Mark");
        contactMessages.Should().ContainSingle()
            .Which.Should().Contain("real one");
    }

    // ── J.5d: ConfabulationInvariant.AppliesTo includes Reflection ────────

    [Fact]
    public void ConfabulationInvariant_AppliesTo_Reflection_AtPersistedSummarySink()
    {
        var inv = new Loops.Invariants.ConfabulationInvariant(
            new Moq.Mock<LearnedGeek.ML.Interfaces.ITextClassificationService>().Object,
            Microsoft.Extensions.Options.Options.Create(new AniRuntime.Core.AniOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Loops.Invariants.ConfabulationInvariant>.Instance);

        var artifact = new CognitiveArtifact
        {
            Content      = "any reflection observation",
            ProducerKind = CognitiveProducerKind.Reflection,
            IntendedSink = CognitiveOutputSink.PersistedSummary,
        };

        inv.AppliesTo(artifact).Should().BeTrue(
            "J.5d adds Reflection to the confab-invariant applicable set — reflections " +
            "write to Semantic where downstream retrieval treats them as factual grounding.");
    }

    [Fact]
    public void AntiParrotInvariant_AppliesTo_Reflection_RemainsFalse()
    {
        // J.5d explicitly does NOT add Reflection to anti-parrot's applicable
        // set — reflection legitimately recalls contact text; a 7-token
        // verbatim run isn't a parrot in this surface.
        var inv = new Loops.Invariants.AntiParrotInvariant();

        var artifact = new CognitiveArtifact
        {
            Content      = "Mark mentioned the bookstore was closing early",
            ProducerKind = CognitiveProducerKind.Reflection,
            IntendedSink = CognitiveOutputSink.PersistedSummary,
        };

        inv.AppliesTo(artifact).Should().BeFalse(
            "anti-parrot intentionally excludes Reflection — recalling contact text in a " +
            "reflection isn't a parrot, it's reflection. Confabulation invariant carries " +
            "this surface.");
    }
}
