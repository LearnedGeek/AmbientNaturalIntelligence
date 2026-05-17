using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Phase 6 v1.2 R3 (May 17, 2026) — pin the contract for the structured
/// reflection-summary JSON parser. The parser is the structural seam that
/// keeps reflection output from drifting back into free-prose first-person
/// inner-thought register. Edge cases here are load-bearing: a regression
/// to free-prose parsing would re-introduce the May 17 bookstore-content-
/// in-substrate problem.
/// </summary>
public class ParseReflectionSummariesTests
{
    [Fact]
    public void Parse_ValidSchema_ReturnsTopicShapeLines()
    {
        var json = """
            {
              "summaries": [
                {"topic": "evening solitude", "shape": "warm-quiet, gently lonely"},
                {"topic": "Mark's morning routine", "shape": "warm-attentive at distance"}
              ]
            }
            """;

        var result = ReflectionPhase.ParseReflectionSummaries(json);

        result.Should().HaveCount(2);
        result[0].Should().Be("[topic: evening solitude] warm-quiet, gently lonely");
        result[1].Should().Be("[topic: Mark's morning routine] warm-attentive at distance");
    }

    [Fact]
    public void Parse_EmptySummariesArray_ReturnsEmptyList()
    {
        var json = """{"summaries": []}""";

        var result = ReflectionPhase.ParseReflectionSummaries(json);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsEmptyList()
    {
        var json = "this is not json at all { broken";

        var result = ReflectionPhase.ParseReflectionSummaries(json);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MissingSummariesKey_ReturnsEmptyList()
    {
        var json = """{"observations": ["something"]}""";

        var result = ReflectionPhase.ParseReflectionSummaries(json);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EntryMissingTopicOrShape_SkipsEntry()
    {
        var json = """
            {
              "summaries": [
                {"topic": "good entry", "shape": "valid shape"},
                {"topic": "no shape"},
                {"shape": "no topic"},
                {"topic": "", "shape": "empty topic"},
                {"topic": "another good", "shape": "still valid"}
              ]
            }
            """;

        var result = ReflectionPhase.ParseReflectionSummaries(json);

        result.Should().HaveCount(2);
        result[0].Should().Be("[topic: good entry] valid shape");
        result[1].Should().Be("[topic: another good] still valid");
    }

    [Fact]
    public void Parse_LongShape_TruncatesTo200Chars()
    {
        // Defensive cap — the prompt asks for ≤120 chars, but the model can
        // over-shoot. The truncation prevents a runaway summary from
        // smuggling verbatim source content through the "shape" field, which
        // would re-introduce the May 17 substrate-pollution failure mode.
        var longShape = new string('x', 300);
        var json = $$"""
            {
              "summaries": [
                {"topic": "test", "shape": "{{longShape}}"}
              ]
            }
            """;

        var result = ReflectionPhase.ParseReflectionSummaries(json);

        result.Should().HaveCount(1);
        result[0].Length.Should().BeLessThanOrEqualTo("[topic: test] ".Length + 200);
    }

    [Fact]
    public void Parse_MoreThan3Summaries_TakesFirst3()
    {
        var json = """
            {
              "summaries": [
                {"topic": "one",   "shape": "first"},
                {"topic": "two",   "shape": "second"},
                {"topic": "three", "shape": "third"},
                {"topic": "four",  "shape": "fourth"},
                {"topic": "five",  "shape": "fifth"}
              ]
            }
            """;

        var result = ReflectionPhase.ParseReflectionSummaries(json);

        result.Should().HaveCount(3);
        result[0].Should().Contain("first");
        result[1].Should().Contain("second");
        result[2].Should().Contain("third");
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyList()
    {
        ReflectionPhase.ParseReflectionSummaries("").Should().BeEmpty();
        ReflectionPhase.ParseReflectionSummaries("   ").Should().BeEmpty();
    }
}

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
