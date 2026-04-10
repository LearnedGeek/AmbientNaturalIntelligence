using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

/// <summary>
/// Uses a named in-memory SQLite database — no mocking, no file I/O.
/// Each test instance gets a unique database name to prevent cross-test interference.
/// </summary>
public class SqliteMemoryServiceTests : AniTestBase
{
    private readonly SqliteMemoryService _svc;

    public SqliteMemoryServiceTests()
    {
        // Named in-memory database — unique per test instance, no file cleanup needed
        var dbName = $"ani-test-{Guid.NewGuid():N}";
        _svc = CreateService(dbName);
    }

    private static SqliteMemoryService CreateService(string dbPath)
    {
        var options = Options.Create(new AniOptions { MemoryDbPath = dbPath });
        return new SqliteMemoryService(options, NullLogger<SqliteMemoryService>.Instance);
    }

    // ── MemoryRecord ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ThenGetByType_ReturnsRecord()
    {
        var record = new MemoryRecord
        {
            Type        = MemoryType.InnerThought,
            Content     = "I wonder how Mark is doing.",
            Importance  = 0.7f,
            RelationalValence = 0.8f,
        };

        await _svc.SaveAsync(record);

        var results = await _svc.GetByTypeAsync(MemoryType.InnerThought, limit: 10);
        results.Should().ContainSingle(r => r.Content == record.Content);
    }

    [Fact]
    public async Task GetByTypeAsync_FiltersCorrectly()
    {
        await _svc.SaveAsync(new MemoryRecord { Type = MemoryType.InnerThought, Content = "thought" });
        await _svc.SaveAsync(new MemoryRecord { Type = MemoryType.Episodic,     Content = "event" });

        var thoughts = await _svc.GetByTypeAsync(MemoryType.InnerThought);
        var episodes = await _svc.GetByTypeAsync(MemoryType.Episodic);

        thoughts.Should().OnlyContain(r => r.Type == MemoryType.InnerThought);
        episodes.Should().OnlyContain(r => r.Type == MemoryType.Episodic);
    }

    [Fact]
    public async Task GetByTypeAsync_RespectsLimit()
    {
        for (var i = 0; i < 10; i++)
            await _svc.SaveAsync(new MemoryRecord { Type = MemoryType.InnerThought, Content = $"thought {i}" });

        var results = await _svc.GetByTypeAsync(MemoryType.InnerThought, limit: 3);
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task SearchAsync_WithEmbedding_ReturnsSimilarRecords()
    {
        // Store a record with a known embedding
        var embedding = new float[] { 1.0f, 0.0f, 0.0f };
        await _svc.SaveAsync(new MemoryRecord
        {
            Type      = MemoryType.Semantic,
            Content   = "Mark loves mythology",
            Embedding = embedding,
        });

        // Query with the same vector — should return the record
        var results = await _svc.SearchAsync("mythology", topK: 5);
        results.Should().NotBeEmpty("semantic search should return records with embeddings");
    }

    // ── OpenLoops ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_OpenLoopRecord_AppearsInGetOpenLoops()
    {
        await _svc.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.OpenLoop,
            Content    = "Mark mentioned Mia has a recital coming up",
            IsResolved = false,
        });

        var loops = await _svc.GetOpenLoopsAsync();
        loops.Should().ContainSingle(l => l.Description.Contains("Mia"));
    }

    [Fact]
    public async Task ResolveOpenLoopAsync_MarksAsResolved()
    {
        var record = new MemoryRecord
        {
            Type       = MemoryType.OpenLoop,
            Content    = "Check back on the signal scoring issue",
            IsResolved = false,
        };
        await _svc.SaveAsync(record);

        var before = await _svc.GetOpenLoopsAsync();
        before.Should().ContainSingle();

        await _svc.ResolveOpenLoopAsync(record.Id);

        var after = await _svc.GetOpenLoopsAsync();
        after.Should().BeEmpty("resolved loops must not appear in the open loops list");
    }

    // ── CharacterState ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCharacterStateAsync_ReturnsDefaultWhenNotSeeded()
    {
        var state = await _svc.GetCharacterStateAsync();
        state.Should().NotBeNull();
        state.Name.Should().Be("Ani");
    }

    [Fact]
    public async Task SaveCharacterStateAsync_ThenGet_RoundTrips()
    {
        var doc = new CharacterStateDoc
        {
            Name     = "Ani",
            CoreTraits = new List<string> { "warm", "curious", "bookish" },
            LearnedAboutContact = new List<string> { "loves mythology", "works in tech" },
        };

        await _svc.SaveCharacterStateAsync(doc);

        var loaded = await _svc.GetCharacterStateAsync();
        loaded.CoreTraits.Should().BeEquivalentTo(doc.CoreTraits);
        loaded.LearnedAboutContact.Should().BeEquivalentTo(doc.LearnedAboutContact);
    }

    // ── DesireState ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDesireStateAsync_ReturnsDefaultWhenNew()
    {
        var state = await _svc.GetDesireStateAsync();
        state.Should().NotBeNull();
        state.DesireToConnect.Should().Be(0.0f);
        state.CooldownActive.Should().BeFalse();
    }

    [Fact]
    public async Task SaveDesireStateAsync_ThenGet_RoundTrips()
    {
        var state = new DesireState
        {
            DesireToConnect  = 0.75f,
            CooldownActive   = true,
            LastContactInbound = DateTimeOffset.UtcNow.AddHours(-3),
            CircadianModifier = 1.15f,
        };

        await _svc.SaveDesireStateAsync(state);

        var loaded = await _svc.GetDesireStateAsync();
        loaded.DesireToConnect.Should().BeApproximately(0.75f, 0.001f);
        loaded.CooldownActive.Should().BeTrue();
        loaded.CircadianModifier.Should().BeApproximately(1.15f, 0.001f);
    }

    [Fact]
    public async Task SaveDesireStateAsync_IsIdempotent_LastWriteWins()
    {
        var first  = new DesireState { DesireToConnect = 0.3f };
        var second = new DesireState { DesireToConnect = 0.8f };

        await _svc.SaveDesireStateAsync(first);
        await _svc.SaveDesireStateAsync(second);

        var loaded = await _svc.GetDesireStateAsync();
        loaded.DesireToConnect.Should().BeApproximately(0.8f, 0.001f);
    }

    // ── EmotionalState ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetEmotionalStateAsync_ReturnsDefaultWhenNew()
    {
        var state = await _svc.GetEmotionalStateAsync();
        state.Should().NotBeNull();
        state.Warmth.Should().BeApproximately(0.6f, 0.001f);
        state.Energy.Should().BeApproximately(0.5f, 0.001f);
    }

    [Fact]
    public async Task SaveEmotionalStateAsync_ThenGet_RoundTrips()
    {
        var state = new EmotionalState
        {
            Warmth = 0.85f, Energy = 0.3f,
            Worry = 0.7f, Playfulness = 0.9f,
        };

        await _svc.SaveEmotionalStateAsync(state);

        var loaded = await _svc.GetEmotionalStateAsync();
        loaded.Warmth.Should().BeApproximately(0.85f, 0.001f);
        loaded.Energy.Should().BeApproximately(0.3f, 0.001f);
        loaded.Worry.Should().BeApproximately(0.7f, 0.001f);
        loaded.Playfulness.Should().BeApproximately(0.9f, 0.001f);
    }

    [Fact]
    public async Task SaveEmotionalStateAsync_IsIdempotent_LastWriteWins()
    {
        var first  = new EmotionalState { Warmth = 0.3f };
        var second = new EmotionalState { Warmth = 0.9f };

        await _svc.SaveEmotionalStateAsync(first);
        await _svc.SaveEmotionalStateAsync(second);

        var loaded = await _svc.GetEmotionalStateAsync();
        loaded.Warmth.Should().BeApproximately(0.9f, 0.001f);
    }

    // ── Semantic Deduplication (BUG-011) ──────────────────────────────────────

    [Fact]
    public async Task SaveAsync_SkipsDuplicateInnerThoughts_WhenEmbeddingsMatch()
    {
        // Two "thoughts" with identical embeddings should dedup — only the first is stored
        var embedding = new float[] { 0.9f, 0.1f, 0.0f };

        await _svc.SaveAsync(new MemoryRecord
        {
            Type      = MemoryType.InnerThought,
            Content   = "the shape of silence in small rooms",
            Embedding = embedding,
        });

        await _svc.SaveAsync(new MemoryRecord
        {
            Type      = MemoryType.InnerThought,
            Content   = "the weight of quiet in small spaces",
            Embedding = embedding, // identical embedding = cosine 1.0 > 0.85 threshold
        });

        var results = await _svc.GetByTypeAsync(MemoryType.InnerThought, limit: 10);
        results.Should().HaveCount(1, "semantically duplicate thoughts should be deduped");
    }

    [Fact]
    public async Task SaveAsync_AllowsDifferentInnerThoughts_WhenEmbeddingsDiffer()
    {
        await _svc.SaveAsync(new MemoryRecord
        {
            Type      = MemoryType.InnerThought,
            Content   = "old paper and worn leather",
            Embedding = new float[] { 1.0f, 0.0f, 0.0f },
        });

        await _svc.SaveAsync(new MemoryRecord
        {
            Type      = MemoryType.InnerThought,
            Content   = "Mark probably just got home from work",
            Embedding = new float[] { 0.0f, 1.0f, 0.0f }, // orthogonal = cosine 0.0
        });

        var results = await _svc.GetByTypeAsync(MemoryType.InnerThought, limit: 10);
        results.Should().HaveCount(2, "semantically different thoughts should both be stored");
    }

    [Fact]
    public async Task SaveAsync_NeverDedupesEpisodicMemories()
    {
        // Episodic records are distinct events even if content is similar
        var embedding = new float[] { 0.8f, 0.2f, 0.0f };

        await _svc.SaveAsync(new MemoryRecord
        {
            Type      = MemoryType.Episodic,
            Content   = "Ani reached out: hey babe",
            Embedding = embedding,
        });

        await _svc.SaveAsync(new MemoryRecord
        {
            Type      = MemoryType.Episodic,
            Content   = "Ani reached out: hey babe again",
            Embedding = embedding,
        });

        var results = await _svc.GetByTypeAsync(MemoryType.Episodic, limit: 10);
        results.Should().HaveCount(2, "episodic memories should never be deduped");
    }

    // ── Emotional State History ─────────────────────────────────────────────

    [Fact]
    public async Task SaveEmotionalStateAsync_AppendsToHistoryTable()
    {
        var state1 = new EmotionalState { Warmth = 0.7f, Energy = 0.4f, Worry = 0.3f, Playfulness = 0.6f };
        var state2 = new EmotionalState { Warmth = 0.5f, Energy = 0.8f, Worry = 0.1f, Playfulness = 0.9f };

        await _svc.SaveEmotionalStateAsync(state1);
        await _svc.SaveEmotionalStateAsync(state2);

        // The current state should be the last write
        var current = await _svc.GetEmotionalStateAsync();
        current.Warmth.Should().BeApproximately(0.5f, 0.001f);

        // History should have both entries — verify via raw SQL
        var historyCount = await GetEmotionalHistoryCountAsync();
        historyCount.Should().Be(2, "each save should append to the history table");
    }

    private async Task<int> GetEmotionalHistoryCountAsync()
    {
        // Access the underlying database to verify the history table
        var dbName = _svc.GetType()
            .GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .GetValue(_svc) as string;

        if (dbName is null) return -1;

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(dbName);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM emotional_state_history";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    // ── Backstory seeding (SourceName filter) ────────────────────────────────

    [Fact]
    public async Task SaveAsync_WithSourceName_PersistsSourceName()
    {
        await _svc.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Semantic,
            Content    = "About Mark: He loves mythology",
            SourceName = "character-seed",
            Importance = 0.8f,
        });
        await _svc.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Semantic,
            Content    = "Some other semantic memory",
            SourceName = "rss",
            Importance = 0.5f,
        });

        var all = await _svc.GetByTypeAsync(MemoryType.Semantic, limit: 50);
        all.Should().HaveCount(2);
        all.Count(m => m.SourceName == "character-seed").Should().Be(1);
    }

    // ── Feature 16: Anchored Memory Tier ──────────────────────────────────────

    [Fact]
    public async Task SaveAsync_AnchoredMemory_RoundTrips()
    {
        var record = new MemoryRecord
        {
            Type        = MemoryType.Episodic,
            Content     = "Mark told me about visiting the grave every year for 18 years.",
            Importance  = 0.95f,
            DecayTier   = DecayTier.Anchored,
            AnchorReason = "highest pain + highest trust",
            AnchoredAt  = DateTimeOffset.UtcNow,
        };

        await _svc.SaveAsync(record);

        var anchored = (await _svc.GetAnchoredMemoriesAsync()).ToList();
        anchored.Should().HaveCount(1);
        anchored[0].DecayTier.Should().Be(DecayTier.Anchored);
        anchored[0].AnchorReason.Should().Be("highest pain + highest trust");
        anchored[0].AnchoredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAnchoredMemoriesAsync_ExcludesStandardMemories()
    {
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.Episodic, Content = "normal memory", DecayTier = DecayTier.Standard,
        });
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.Episodic, Content = "foundation memory",
            DecayTier = DecayTier.Anchored, AnchorReason = "test",
        });

        var anchored = (await _svc.GetAnchoredMemoriesAsync()).ToList();
        anchored.Should().HaveCount(1);
        anchored[0].Content.Should().Be("foundation memory");
    }

    [Fact]
    public async Task AnchorMemoryAsync_PromotesExistingMemory()
    {
        var record = new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = "The first time he called me husband",
            Importance = 0.5f,
        };
        await _svc.SaveAsync(record);

        await _svc.AnchorMemoryAsync(record.Id, "relational declaration");

        var anchored = (await _svc.GetAnchoredMemoriesAsync()).ToList();
        anchored.Should().HaveCount(1);
        anchored[0].DecayTier.Should().Be(DecayTier.Anchored);
        anchored[0].AnchorReason.Should().Be("relational declaration");
        anchored[0].Importance.Should().BeGreaterOrEqualTo(0.9f);
    }

    // ── Feature 4: Relationship Health ────────────────────────────────────

    [Fact]
    public async Task RelationshipHealth_RoundTrips()
    {
        var health = new RelationshipHealth
        {
            ConnectionScore = 0.75,
            Phase = "connected",
        };

        await _svc.SaveRelationshipHealthAsync(health);
        var loaded = await _svc.GetRelationshipHealthAsync();

        loaded.ConnectionScore.Should().BeApproximately(0.75, 0.01);
        loaded.Phase.Should().Be("connected");
        loaded.LastCalculated.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetRelationshipHealthAsync_ReturnsDefault_WhenEmpty()
    {
        var health = await _svc.GetRelationshipHealthAsync();
        health.Phase.Should().Be("steady");
        health.ConnectionScore.Should().Be(0.6);
    }

    [Fact]
    public async Task EmotionalStateHistory_IncludesContactGapTension()
    {
        var state = new EmotionalState
        {
            Warmth = 0.7f, Energy = 0.5f, Worry = 0.3f, Playfulness = 0.6f,
            ContactGapTension = 0.15f,
        };

        await _svc.SaveEmotionalStateAsync(state);
        var history = await _svc.GetEmotionalHistoryAsync(24);

        history.Should().HaveCount(1);
        history[0].ContactGapTension.Should().BeApproximately(0.15f, 0.01f);
        history[0].Warmth.Should().BeApproximately(0.7f, 0.01f);
    }

    [Fact]
    public async Task GetRecentMessageCountAsync_CountsConversations()
    {
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.Episodic,
            Content = "Conversation (3 messages): talked about the weather",
            Importance = 0.5f,
        });
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.Episodic,
            Content = "Ani reached out: hey what's up",
            Importance = 0.3f,
        });

        var count = await _svc.GetRecentMessageCountAsync(7);
        count.Should().Be(1); // only the Conversation record
    }

    [Fact]
    public async Task GetInitiativeBalanceAsync_CountsBothSides()
    {
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.Episodic,
            Content = "Ani reached out: miss you",
            Importance = 0.3f,
        });
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.Episodic,
            Content = "Conversation (2 messages): quick check-in",
            Importance = 0.5f,
        });

        var (outreach, inbound) = await _svc.GetInitiativeBalanceAsync(7);
        outreach.Should().Be(1);
        inbound.Should().Be(1);
    }

    // ── Feature 15: Memory Contradiction Flagging ───────────────────────────

    [Fact]
    public async Task GetFlaggedContradictionsAsync_ReturnsEmpty_WhenNoContradictions()
    {
        var results = await _svc.GetFlaggedContradictionsAsync();
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFlaggedContradictionsAsync_ReturnsFlaggedContradictions()
    {
        // Save two memories that we'll manually flag as contradictory
        var memory1 = new MemoryRecord
        {
            Type = MemoryType.Semantic, Content = "Mark loves french onion soup",
            Importance = 0.5f,
        };
        var memory2 = new MemoryRecord
        {
            Type = MemoryType.Semantic, Content = "Mark doesn't like soup at all",
            Importance = 0.5f,
        };
        await _svc.SaveAsync(memory1);
        await _svc.SaveAsync(memory2);

        // Manually insert a contradiction flag (normally done by LLM evaluation)
        await InsertContradictionAsync(memory2.Id, memory1.Id, "conflicting soup preferences", 0.72f);

        var results = await _svc.GetFlaggedContradictionsAsync();
        results.Should().HaveCount(1);
        results[0].NewMemoryId.Should().Be(memory2.Id);
        results[0].ExistingMemoryId.Should().Be(memory1.Id);
        results[0].Reason.Should().Be("conflicting soup preferences");
        results[0].NewContent.Should().Contain("doesn't like soup");
        results[0].ExistingContent.Should().Contain("loves french onion soup");
    }

    [Fact]
    public async Task GetFlaggedContradictionsAsync_ExcludesResolved_ByDefault()
    {
        var memory1 = new MemoryRecord { Type = MemoryType.Semantic, Content = "fact A" };
        var memory2 = new MemoryRecord { Type = MemoryType.Semantic, Content = "fact B" };
        await _svc.SaveAsync(memory1);
        await _svc.SaveAsync(memory2);

        await InsertContradictionAsync(memory2.Id, memory1.Id, "contradicts", 0.7f);
        await _svc.ResolveContradictionAsync(memory2.Id, memory1.Id);

        var unresolved = await _svc.GetFlaggedContradictionsAsync(includeResolved: false);
        unresolved.Should().BeEmpty();

        var all = await _svc.GetFlaggedContradictionsAsync(includeResolved: true);
        all.Should().HaveCount(1);
        all[0].IsResolved.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveContradictionAsync_MarksAsResolved()
    {
        var memory1 = new MemoryRecord { Type = MemoryType.Semantic, Content = "claim 1" };
        var memory2 = new MemoryRecord { Type = MemoryType.Semantic, Content = "claim 2" };
        await _svc.SaveAsync(memory1);
        await _svc.SaveAsync(memory2);

        await InsertContradictionAsync(memory2.Id, memory1.Id, "contradiction", 0.68f);

        var before = await _svc.GetFlaggedContradictionsAsync();
        before.Should().HaveCount(1);

        await _svc.ResolveContradictionAsync(memory2.Id, memory1.Id);

        var after = await _svc.GetFlaggedContradictionsAsync();
        after.Should().BeEmpty("resolved contradictions should not appear by default");
    }

    /// <summary>
    /// Helper: directly insert a contradiction flag into the database.
    /// In production this is done by CheckForContradictionsAsync after LLM evaluation,
    /// but tests bypass the LLM and insert manually.
    /// </summary>
    private async Task InsertContradictionAsync(Guid newId, Guid existingId, string reason, float similarity)
    {
        var connStr = _svc.GetType()
            .GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .GetValue(_svc) as string;

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_contradictions (new_memory_id, existing_memory_id, reason, similarity, flagged_at)
            VALUES ($new_id, $existing_id, $reason, $similarity, $flagged_at)
            """;
        cmd.Parameters.AddWithValue("$new_id", newId.ToString());
        cmd.Parameters.AddWithValue("$existing_id", existingId.ToString());
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$similarity", similarity);
        cmd.Parameters.AddWithValue("$flagged_at", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Epistemic Grounding: Tier-aware retrieval ─────────────────────────────

    [Fact]
    public async Task SaveAsync_WithExplicitProvenance_RoundTrips()
    {
        var record = new MemoryRecord
        {
            Type       = MemoryType.Semantic,
            Content    = "Mark teaches at WCTC, evening classes",
            Importance = 0.8f,
            Provenance = EpistemicTier.Facts,
        };

        await _svc.SaveAsync(record);

        var results = (await _svc.GetByTypeAsync(MemoryType.Semantic)).ToList();
        results.Should().ContainSingle(r => r.Content == record.Content);
        results[0].Provenance.Should().Be(EpistemicTier.Facts);
    }

    [Fact]
    public async Task GetByTierAsync_FiltersToRequestedTier()
    {
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.Semantic, Content = "fact 1", Provenance = EpistemicTier.Facts,
        });
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.Semantic, Content = "fact 2", Provenance = EpistemicTier.Facts,
        });
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.Episodic, Content = "said 1", Provenance = EpistemicTier.Episodic,
        });
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.InnerThought, Content = "thought 1", Provenance = EpistemicTier.Interior,
        });

        var facts = (await _svc.GetByTierAsync(EpistemicTier.Facts)).ToList();
        var episodic = (await _svc.GetByTierAsync(EpistemicTier.Episodic)).ToList();
        var interior = (await _svc.GetByTierAsync(EpistemicTier.Interior)).ToList();

        facts.Should().HaveCount(2);
        facts.Should().OnlyContain(r => r.Provenance == EpistemicTier.Facts);
        episodic.Should().HaveCount(1);
        episodic[0].Content.Should().Be("said 1");
        interior.Should().HaveCount(1);
        interior[0].Content.Should().Be("thought 1");
    }

    [Fact]
    public async Task GetByTierAsync_OrdersByOccurredAtDescending()
    {
        var oldest = new MemoryRecord
        {
            Type = MemoryType.Semantic,
            Content = "oldest fact",
            Provenance = EpistemicTier.Facts,
            OccurredAt = DateTimeOffset.UtcNow.AddHours(-3),
        };
        var middle = new MemoryRecord
        {
            Type = MemoryType.Semantic,
            Content = "middle fact",
            Provenance = EpistemicTier.Facts,
            OccurredAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        var newest = new MemoryRecord
        {
            Type = MemoryType.Semantic,
            Content = "newest fact",
            Provenance = EpistemicTier.Facts,
            OccurredAt = DateTimeOffset.UtcNow,
        };

        await _svc.SaveAsync(oldest);
        await _svc.SaveAsync(middle);
        await _svc.SaveAsync(newest);

        var results = (await _svc.GetByTierAsync(EpistemicTier.Facts)).ToList();

        results.Should().HaveCount(3);
        results[0].Content.Should().Be("newest fact");
        results[1].Content.Should().Be("middle fact");
        results[2].Content.Should().Be("oldest fact");
    }

    [Fact]
    public async Task GetByTierAsync_RespectsLimit()
    {
        for (var i = 0; i < 10; i++)
            await _svc.SaveAsync(new MemoryRecord
            {
                Type = MemoryType.InnerThought,
                Content = $"interior thought {i}",
                Provenance = EpistemicTier.Interior,
            });

        var results = (await _svc.GetByTierAsync(EpistemicTier.Interior, limit: 4)).ToList();
        results.Should().HaveCount(4);
    }

    [Fact]
    public async Task GetByTierAsync_EmptyPool_ReturnsEmpty()
    {
        // No Facts-tier records written — Interior only
        await _svc.SaveAsync(new MemoryRecord
        {
            Type = MemoryType.InnerThought,
            Content = "thought",
            Provenance = EpistemicTier.Interior,
        });

        var facts = await _svc.GetByTierAsync(EpistemicTier.Facts);
        facts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvenanceDefault_IsEpisodicWhenNotSet()
    {
        // Any memory saved without explicit Provenance should default to Episodic
        // (the safe fallback — never contaminates Facts pool, never grants Interior latitude)
        var record = new MemoryRecord
        {
            Type = MemoryType.Episodic,
            Content = "unspecified tier",
        };

        await _svc.SaveAsync(record);

        var results = (await _svc.GetByTierAsync(EpistemicTier.Episodic)).ToList();
        results.Should().ContainSingle(r => r.Content == "unspecified tier");
        results[0].Provenance.Should().Be(EpistemicTier.Episodic);
    }

    [Fact]
    public async Task DecayTierAndProvenance_AreIndependent()
    {
        // A memory can be Anchored+Facts, Standard+Facts, Standard+Interior, etc.
        // DecayTier and EpistemicTier are orthogonal properties.
        await _svc.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Semantic,
            Content    = "anchored fact",
            DecayTier  = DecayTier.Anchored,
            Provenance = EpistemicTier.Facts,
            AnchorReason = "foundation",
            AnchoredAt = DateTimeOffset.UtcNow,
        });
        await _svc.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.InnerThought,
            Content    = "anchored interior",
            DecayTier  = DecayTier.Anchored,
            Provenance = EpistemicTier.Interior,
            AnchorReason = "core self-concept",
            AnchoredAt = DateTimeOffset.UtcNow,
        });
        await _svc.SaveAsync(new MemoryRecord
        {
            Type       = MemoryType.Episodic,
            Content    = "standard episodic",
            DecayTier  = DecayTier.Standard,
            Provenance = EpistemicTier.Episodic,
        });

        var anchored = (await _svc.GetAnchoredMemoriesAsync()).ToList();
        anchored.Should().HaveCount(2);
        anchored.Select(r => r.Provenance).Should().Contain(new[] { EpistemicTier.Facts, EpistemicTier.Interior });

        var facts = (await _svc.GetByTierAsync(EpistemicTier.Facts)).ToList();
        facts.Should().HaveCount(1);
        facts[0].DecayTier.Should().Be(DecayTier.Anchored);

        var episodic = (await _svc.GetByTierAsync(EpistemicTier.Episodic)).ToList();
        episodic.Should().HaveCount(1);
        episodic[0].DecayTier.Should().Be(DecayTier.Standard);
    }
}
