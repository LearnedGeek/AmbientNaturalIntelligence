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
            MarkValence = 0.8f,
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
            LearnedAboutMark = new List<string> { "loves mythology", "works in tech" },
        };

        await _svc.SaveCharacterStateAsync(doc);

        var loaded = await _svc.GetCharacterStateAsync();
        loaded.CoreTraits.Should().BeEquivalentTo(doc.CoreTraits);
        loaded.LearnedAboutMark.Should().BeEquivalentTo(doc.LearnedAboutMark);
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
            LastMarkContact  = DateTimeOffset.UtcNow.AddHours(-3),
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
            Concern = 0.7f, Playfulness = 0.9f,
        };

        await _svc.SaveEmotionalStateAsync(state);

        var loaded = await _svc.GetEmotionalStateAsync();
        loaded.Warmth.Should().BeApproximately(0.85f, 0.001f);
        loaded.Energy.Should().BeApproximately(0.3f, 0.001f);
        loaded.Concern.Should().BeApproximately(0.7f, 0.001f);
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
}
