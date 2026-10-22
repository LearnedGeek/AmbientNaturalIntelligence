using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 1 — pins the growth-edge mutator's bounded-delta discipline,
/// new-key threshold, prune pass, and parse surface. Does NOT hit a live
/// Ollama endpoint; LLM extraction quality is a separate empirical check
/// (30-day post-deploy audit per plan doc §Phase 1 acceptance).
/// </summary>
public class OllamaGrowthEdgeMutatorTests
{
    private static OllamaGrowthEdgeMutator BuildMutator(
        string ollamaJsonResponse,
        AniOptions? options = null)
    {
        var mock = new Mock<IOllamaClient>();
        mock.Setup(o => o.ChatJsonWithModelAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(ollamaJsonResponse);

        var opts = Options.Create(options ?? new AniOptions
        {
            GrowthEdgesEnabled = true,
            GrowthEdgeMaxDeltaPerCycle = 0.05f,
            GrowthEdgeNewKeyBaseWeight = 0.3f,
            GrowthEdgeNewKeyThreshold = 0.4f,
            GrowthEdgePruneThreshold = 0.05f,
            GrowthEdgeMaxKeys = 50,
            GrowthEdgeMutatorModel = "qwen3:14b",
        });

        return new OllamaGrowthEdgeMutator(mock.Object, opts, NullLogger<OllamaGrowthEdgeMutator>.Instance);
    }

    private static CharacterStateDoc StateWith(params (string Key, float Weight)[] entries)
    {
        var cs = new CharacterStateDoc();
        foreach (var (k, w) in entries) cs.TopicValence[k] = w;
        return cs;
    }

    private static IReadOnlyList<MemoryRecord> AnyThoughts(int n = 3)
    {
        var list = new List<MemoryRecord>();
        for (var i = 0; i < n; i++)
            list.Add(new MemoryRecord { Content = $"thought {i}", Type = MemoryType.InnerThought });
        return list;
    }

    [Fact]
    public async Task UpdateTopicValenceAsync_EmptyThoughts_ReturnsEmpty()
    {
        var mutator = BuildMutator("{\"topics\":[]}");
        var result = await mutator.UpdateTopicValenceAsync(
            new CharacterStateDoc(), Array.Empty<MemoryRecord>(), CancellationToken.None);

        result.Should().Be(GrowthEdgeUpdateResult.Empty);
    }

    [Fact]
    public async Task UpdateTopicValenceAsync_TransportFailure_ReturnsEmptyFailOpen()
    {
        var mock = new Mock<IOllamaClient>();
        mock.Setup(o => o.ChatJsonWithModelAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("simulated transport failure"));

        var opts = Options.Create(new AniOptions());
        var mutator = new OllamaGrowthEdgeMutator(mock.Object, opts, NullLogger<OllamaGrowthEdgeMutator>.Instance);

        var cs = StateWith(("Mia", 0.9f));
        var result = await mutator.UpdateTopicValenceAsync(cs, AnyThoughts(), CancellationToken.None);

        result.KeysUpdated.Should().Be(0);
        result.NewKeysAdded.Should().Be(0);
        cs.TopicValence["Mia"].Should().Be(0.9f, "transport failure must leave dictionary unchanged");
    }

    [Fact]
    public async Task UpdateTopicValenceAsync_MalformedJson_ReturnsEmpty()
    {
        var mutator = BuildMutator("not json at all");
        var cs = StateWith(("Mia", 0.9f));

        var result = await mutator.UpdateTopicValenceAsync(cs, AnyThoughts(), CancellationToken.None);

        result.Should().Be(GrowthEdgeUpdateResult.Empty);
        cs.TopicValence["Mia"].Should().Be(0.9f);
    }

    // ── Anchor promotion (Phase 4a.2c) ─────────────────────────────────────

    [Fact]
    public async Task UpdateTopicValenceAsync_AnchorAboveThreshold_PromotedAsNewKey()
    {
        // LLM returns no topics; anchor pathway alone drives the mutation.
        var mutator = BuildMutator("{\"topics\":[]}", new AniOptions
        {
            GrowthEdgesEnabled              = true,
            GrowthEdgeMaxDeltaPerCycle      = 0.05f,
            GrowthEdgeNewKeyBaseWeight      = 0.3f,
            GrowthEdgeNewKeyThreshold       = 0.4f,
            GrowthEdgePruneThreshold        = 0.0f,
            GrowthEdgeMaxKeys               = 50,
            AnchorPromotionEnabled          = true,
            AnchorPromotionMinOccurrences   = 3,
            AnchorPromotionSalience         = 0.7f,
        });

        var cs = new CharacterStateDoc();
        var anchors = new[] { "the shelf", "the shelf", "the shelf", "lila", "cooking" };

        var result = await mutator.UpdateTopicValenceAsync(
            cs, AnyThoughts(), CancellationToken.None, anchors);

        cs.TopicValence.Should().ContainKey("the shelf");
        cs.TopicValence["the shelf"].Should().BeApproximately(0.21f, 0.001f,
            "salience 0.7 × base 0.3 = 0.21 for a brand-new anchor-promoted key");
        result.NewKeysAdded.Should().Be(1);
    }

    [Fact]
    public async Task UpdateTopicValenceAsync_AnchorMatchesExistingKey_BoundedDeltaApplied()
    {
        var mutator = BuildMutator("{\"topics\":[]}", new AniOptions
        {
            GrowthEdgesEnabled              = true,
            GrowthEdgeMaxDeltaPerCycle      = 0.05f,
            GrowthEdgePruneThreshold        = 0.0f,
            GrowthEdgeMaxKeys               = 50,
            AnchorPromotionEnabled          = true,
            AnchorPromotionMinOccurrences   = 3,
            AnchorPromotionSalience         = 0.8f,
        });

        var cs = StateWith(("Mia", 0.5f));
        var anchors = new[] { "mia", "MIA", "Mia" };  // case-insensitive match to existing "Mia"

        var result = await mutator.UpdateTopicValenceAsync(
            cs, AnyThoughts(), CancellationToken.None, anchors);

        // salience 0.8 × max-delta 0.05 = 0.04
        cs.TopicValence["Mia"].Should().BeApproximately(0.54f, 0.001f);
        result.KeysUpdated.Should().Be(1);
        result.NewKeysAdded.Should().Be(0);
    }

    [Fact]
    public async Task UpdateTopicValenceAsync_AnchorFlagOff_IgnoresAnchors()
    {
        var mutator = BuildMutator("{\"topics\":[]}", new AniOptions
        {
            GrowthEdgesEnabled              = true,
            GrowthEdgePruneThreshold        = 0.0f,
            GrowthEdgeMaxKeys               = 50,
            AnchorPromotionEnabled          = false,  // disabled
            AnchorPromotionMinOccurrences   = 3,
        });

        var cs = new CharacterStateDoc();
        var anchors = new[] { "the shelf", "the shelf", "the shelf", "the shelf" };

        var result = await mutator.UpdateTopicValenceAsync(
            cs, AnyThoughts(), CancellationToken.None, anchors);

        cs.TopicValence.Should().BeEmpty();
        result.NewKeysAdded.Should().Be(0);
    }

    [Fact]
    public async Task UpdateTopicValenceAsync_AnchorBelowThreshold_NotPromoted()
    {
        var mutator = BuildMutator("{\"topics\":[]}", new AniOptions
        {
            GrowthEdgesEnabled              = true,
            GrowthEdgePruneThreshold        = 0.0f,
            GrowthEdgeMaxKeys               = 50,
            AnchorPromotionEnabled          = true,
            AnchorPromotionMinOccurrences   = 3,
            AnchorPromotionSalience         = 0.7f,
        });

        var cs = new CharacterStateDoc();
        var anchors = new[] { "the shelf", "the shelf" };  // only 2 occurrences, below 3

        var result = await mutator.UpdateTopicValenceAsync(
            cs, AnyThoughts(), CancellationToken.None, anchors);

        cs.TopicValence.Should().BeEmpty();
        result.NewKeysAdded.Should().Be(0);
    }

    // ── ApplyUpdates ───────────────────────────────────────────────────────
    // These tests exercise the mutation math directly via the internal
    // ApplyUpdates method, so the discipline (bounded delta, new-key
    // threshold, prune) is pinned deterministically without the Ollama hop.

    [Fact]
    public void ApplyUpdates_ExistingKeyMatch_BoundedDeltaApplied()
    {
        var mutator = BuildMutator("{}"); // response doesn't matter for this test
        var cs = StateWith(("Mia", 0.5f));

        var result = mutator.ApplyUpdates(cs, new[]
        {
            new OllamaGrowthEdgeMutator.ExtractedTopic("Mia", Salience: 0.8f, ExistingKeyMatch: "Mia"),
        });

        // delta = 0.8 (salience) * 0.05 (max per cycle) = 0.04
        cs.TopicValence["Mia"].Should().BeApproximately(0.54f, 0.001f);
        result.KeysUpdated.Should().Be(1);
        result.NewKeysAdded.Should().Be(0);
    }

    [Fact]
    public void ApplyUpdates_ExistingKeyAtCeiling_DeltaClampedAtOne()
    {
        var mutator = BuildMutator("{}");
        var cs = StateWith(("Mia", 0.98f));

        var result = mutator.ApplyUpdates(cs, new[]
        {
            new OllamaGrowthEdgeMutator.ExtractedTopic("Mia", 1.0f, "Mia"),
        });

        cs.TopicValence["Mia"].Should().Be(1.0f, "weight clamped at 1.0");
        result.KeysUpdated.Should().Be(1);
    }

    [Fact]
    public void ApplyUpdates_NewTopicAboveThreshold_AddedAtScaledBase()
    {
        var mutator = BuildMutator("{}");
        var cs = new CharacterStateDoc();

        var result = mutator.ApplyUpdates(cs, new[]
        {
            new OllamaGrowthEdgeMutator.ExtractedTopic("lila", 0.7f, ExistingKeyMatch: null),
        });

        // initial = salience 0.7 * base 0.3 = 0.21
        cs.TopicValence.Should().ContainKey("lila");
        cs.TopicValence["lila"].Should().BeApproximately(0.21f, 0.001f);
        result.NewKeysAdded.Should().Be(1);
        result.KeysUpdated.Should().Be(0);
    }

    [Fact]
    public void ApplyUpdates_NewTopicBelowThreshold_NotAdded()
    {
        var mutator = BuildMutator("{}");
        var cs = new CharacterStateDoc();

        var result = mutator.ApplyUpdates(cs, new[]
        {
            new OllamaGrowthEdgeMutator.ExtractedTopic("noise topic", 0.2f, ExistingKeyMatch: null),
        });

        cs.TopicValence.Should().BeEmpty("salience 0.2 below NewKeyThreshold 0.4");
        result.NewKeysAdded.Should().Be(0);
    }

    [Fact]
    public void ApplyUpdates_LowWeightKey_PrunedOnCycle()
    {
        var mutator = BuildMutator("{}");
        var cs = StateWith(
            ("stale noise", 0.03f),  // below prune threshold
            ("Mia", 0.9f));

        var result = mutator.ApplyUpdates(cs, Array.Empty<OllamaGrowthEdgeMutator.ExtractedTopic>());

        cs.TopicValence.Should().NotContainKey("stale noise");
        cs.TopicValence.Should().ContainKey("Mia");
        result.KeysPruned.Should().Be(1);
    }

    [Fact]
    public void ApplyUpdates_ExceedsMaxKeys_TrimsLowestWeighted()
    {
        var opts = new AniOptions { GrowthEdgeMaxKeys = 3, GrowthEdgePruneThreshold = 0.0f };
        var mutator = BuildMutator("{}", opts);

        var cs = StateWith(
            ("high", 0.9f),
            ("mid", 0.5f),
            ("low", 0.2f),
            ("floor", 0.1f),  // 4 entries, max 3
            ("ceiling", 0.95f));  // 5 entries, max 3

        mutator.ApplyUpdates(cs, Array.Empty<OllamaGrowthEdgeMutator.ExtractedTopic>());

        cs.TopicValence.Count.Should().Be(3);
        cs.TopicValence.Should().ContainKeys("ceiling", "high", "mid");
        cs.TopicValence.Should().NotContainKeys("low", "floor");
    }

    [Fact]
    public void ApplyUpdates_UnmatchedExistingKey_IgnoredSilently()
    {
        var mutator = BuildMutator("{}");
        var cs = StateWith(("Mia", 0.9f));

        var result = mutator.ApplyUpdates(cs, new[]
        {
            // Model hallucinated an existing_key_match that isn't in the dictionary;
            // mutator should NOT create a new key under the hallucinated name AND
            // should NOT touch "Mia" (salience was for a different topic).
            new OllamaGrowthEdgeMutator.ExtractedTopic("random", 0.5f, "nonexistent key"),
        });

        cs.TopicValence.Should().HaveCount(1);
        cs.TopicValence["Mia"].Should().Be(0.9f);
        // Note: the mutator doesn't currently fall through to the new-key path when
        // existing_key_match is non-null-but-invalid. That's a defensible choice —
        // trust the model's own match claim; if the match is wrong, that salience
        // signal is lost this cycle but no bad write happens.
        result.KeysUpdated.Should().Be(0);
        result.NewKeysAdded.Should().Be(0);
    }

    // ── ParseExtractedTopics ───────────────────────────────────────────────

    [Fact]
    public void ParseExtractedTopics_WellFormedResponse_ExtractsAllFields()
    {
        var json = """
            {
              "topics": [
                { "topic": "lila", "salience": 0.6, "existing_key_match": null },
                { "topic": "mia at starbucks", "salience": 0.85, "existing_key_match": "Mia" }
              ]
            }
            """;

        var topics = OllamaGrowthEdgeMutator.ParseExtractedTopics(json);

        topics.Should().HaveCount(2);
        topics[0].Topic.Should().Be("lila");
        topics[0].Salience.Should().Be(0.6f);
        topics[0].ExistingKeyMatch.Should().BeNull();
        topics[1].Topic.Should().Be("mia at starbucks");
        topics[1].Salience.Should().Be(0.85f);
        topics[1].ExistingKeyMatch.Should().Be("Mia");
    }

    [Fact]
    public void ParseExtractedTopics_EmptyTopics_ReturnsEmpty()
    {
        var topics = OllamaGrowthEdgeMutator.ParseExtractedTopics("{\"topics\":[]}");
        topics.Should().BeEmpty();
    }

    [Fact]
    public void ParseExtractedTopics_MissingTopicsField_ReturnsEmpty()
    {
        var topics = OllamaGrowthEdgeMutator.ParseExtractedTopics("{\"other\":123}");
        topics.Should().BeEmpty();
    }

    [Fact]
    public void ParseExtractedTopics_MalformedJson_ReturnsEmpty()
    {
        var topics = OllamaGrowthEdgeMutator.ParseExtractedTopics("not json");
        topics.Should().BeEmpty();
    }

    [Fact]
    public void ParseExtractedTopics_PartiallyMalformedEntries_SkipsBadDropsGood()
    {
        var json = """
            {
              "topics": [
                { "salience": 0.5 },
                { "topic": "lila", "salience": 0.6 },
                "not an object",
                { "topic": "", "salience": 0.9 }
              ]
            }
            """;

        var topics = OllamaGrowthEdgeMutator.ParseExtractedTopics(json);

        // First entry: topic missing, no existing_key_match → drops (both empty)
        // Second entry: good → keeps
        // Third entry: not an object → skipped
        // Fourth entry: empty topic and no match → drops
        topics.Should().HaveCount(1);
        topics[0].Topic.Should().Be("lila");
    }
}
