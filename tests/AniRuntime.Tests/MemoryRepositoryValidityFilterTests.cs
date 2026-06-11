using AniRuntime.Core.Models;
using AniRuntime.Memory;
using AniRuntime.Memory.Entities;
using AniRuntime.Memory.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Tests;

/// <summary>
/// G.0 (2026-06-11) — pins the load-bearing Validity='valid' filter invariant
/// across every <see cref="MemoryRepository"/> retrieval path that feeds the
/// prompt-construction layer. Quarantined records (Validity != 'valid' — the
/// J.5h Issue #62 ///tag walkbacks) must never resurface through these
/// methods.
///
/// **Empirical anchor:** 2026-06-11 07:06 outreach where the previously-
/// quarantined "imperfect vs preterite Spanish on Sunday mornings" phrase
/// re-surfaced. Root cause: <see cref="MemoryRepository.GetForSemanticSearchAsync"/>
/// enforced the filter but the parallel
/// <see cref="MemoryRepository.GetForSemanticSearchByTypeAsync"/> and
/// <see cref="MemoryRepository.GetForTierScopedSearchAsync"/> + the
/// recent-memory <see cref="MemoryRepository.GetRecentAsync"/> path did NOT.
///
/// Issue #92 §G.0.
/// </summary>
public class MemoryRepositoryValidityFilterTests
{
    private static DbContextOptions<AniDbContext> InMemoryOptions() =>
        new DbContextOptionsBuilder<AniDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

    private static async Task<AniDbContext> CreateOpenContextAsync()
    {
        var ctx = new AniDbContext(InMemoryOptions());
        await ctx.Database.OpenConnectionAsync();
        await ctx.Database.EnsureCreatedAsync();
        return ctx;
    }

    private static MemoryEntity MakeMemory(string content, string validity, MemoryType type = MemoryType.Episodic,
                                            DecayTier tier = DecayTier.Standard,
                                            EpistemicTier provenance = EpistemicTier.Interior,
                                            bool withEmbedding = true)
    {
        return new MemoryEntity
        {
            Id          = Guid.NewGuid(),
            Type        = type,
            Tier        = tier,
            Provenance  = provenance,
            Content     = content,
            OccurredAt  = DateTimeOffset.UtcNow,
            Validity    = validity,
            Embedding   = withEmbedding ? new float[] { 0.1f, 0.2f, 0.3f } : null,
            Importance  = 0.5f,
        };
    }

    [Fact]
    public async Task GetRecentAsync_ExcludesNonValidRecords()
    {
        await using var ctx = await CreateOpenContextAsync();
        ctx.Memories.Add(MakeMemory("valid record",                "valid"));
        ctx.Memories.Add(MakeMemory("imperfect vs preterite",      "invalid_fabrication"));
        ctx.Memories.Add(MakeMemory("walked-back tag content",     "invalid_walked_back"));
        await ctx.SaveChangesAsync();

        var repo = new MemoryRepository(ctx);
        var recent = await repo.GetRecentAsync(limit: 50);

        recent.Should().HaveCount(1, "only the Validity='valid' record may surface");
        recent[0].Content.Should().Be("valid record");
        recent.Select(m => m.Content).Should().NotContain("imperfect vs preterite",
            "G.0 invariant: quarantined fabrication records must not resurface via GetRecentAsync");
    }

    [Fact]
    public async Task GetForSemanticSearchAsync_ExcludesNonValidRecords()
    {
        await using var ctx = await CreateOpenContextAsync();
        ctx.Memories.Add(MakeMemory("valid embedded record",  "valid"));
        ctx.Memories.Add(MakeMemory("quarantined embedded",   "invalid_fabrication"));
        await ctx.SaveChangesAsync();

        var repo = new MemoryRepository(ctx);
        var candidates = await repo.GetForSemanticSearchAsync();

        candidates.Should().HaveCount(1);
        candidates[0].Content.Should().Be("valid embedded record");
    }

    [Fact]
    public async Task GetForSemanticSearchByTypeAsync_ExcludesNonValidRecords()
    {
        await using var ctx = await CreateOpenContextAsync();
        ctx.Memories.Add(MakeMemory("valid episodic",            "valid",                MemoryType.Episodic));
        ctx.Memories.Add(MakeMemory("quarantined episodic",      "invalid_fabrication",  MemoryType.Episodic));
        ctx.Memories.Add(MakeMemory("valid semantic",            "valid",                MemoryType.Semantic));
        await ctx.SaveChangesAsync();

        var repo = new MemoryRepository(ctx);
        var episodics = await repo.GetForSemanticSearchByTypeAsync(MemoryType.Episodic);

        episodics.Should().HaveCount(1,
            "G.0 invariant: type-scoped retrieval must enforce Validity='valid' to match GetForSemanticSearchAsync");
        episodics[0].Content.Should().Be("valid episodic");
    }

    [Fact]
    public async Task GetForTierScopedSearchAsync_ExcludesNonValidRecords()
    {
        await using var ctx = await CreateOpenContextAsync();
        ctx.Memories.Add(MakeMemory("valid interior",       "valid",                provenance: EpistemicTier.Interior));
        ctx.Memories.Add(MakeMemory("quarantined interior", "invalid_fabrication",  provenance: EpistemicTier.Interior));
        ctx.Memories.Add(MakeMemory("valid facts",          "valid",                provenance: EpistemicTier.Facts));
        await ctx.SaveChangesAsync();

        var repo = new MemoryRepository(ctx);
        var interior = await repo.GetForTierScopedSearchAsync(EpistemicTier.Interior);

        interior.Should().HaveCount(1,
            "G.0 invariant: tier-scoped retrieval must enforce Validity='valid' to match GetForSemanticSearchAsync");
        interior[0].Content.Should().Be("valid interior");
    }

    [Fact]
    public async Task QuarantinedRecord_DoesNotResurface_AcrossAnyRetrievalPath()
    {
        // G.0 invariant — single end-to-end assertion: a quarantined record
        // (the empirical "imperfect vs preterite" 2026-05-24 fabrication shape)
        // must not surface through ANY of the four retrieval paths that feed
        // [INTERIOR] / RecentMemory / [FACTS] in the prompt-construction layer.
        await using var ctx = await CreateOpenContextAsync();

        const string quarantinedPhrase = "i know you've been struggling with imperfect vs preterite in spanish";
        ctx.Memories.Add(MakeMemory(quarantinedPhrase, "invalid_fabrication"));
        ctx.Memories.Add(MakeMemory("anchor — valid record",  "valid"));
        await ctx.SaveChangesAsync();

        var repo = new MemoryRepository(ctx);

        var recent      = (await repo.GetRecentAsync(50)).Select(m => m.Content);
        var semantic    = (await repo.GetForSemanticSearchAsync()).Select(m => m.Content);
        var byType      = (await repo.GetForSemanticSearchByTypeAsync(MemoryType.Episodic)).Select(m => m.Content);
        var byTier      = (await repo.GetForTierScopedSearchAsync(EpistemicTier.Interior)).Select(m => m.Content);

        recent.Should().NotContain(c => c.Contains(quarantinedPhrase),
            "G.0: quarantined record must not surface via GetRecentAsync");
        semantic.Should().NotContain(c => c.Contains(quarantinedPhrase),
            "G.0: quarantined record must not surface via GetForSemanticSearchAsync");
        byType.Should().NotContain(c => c.Contains(quarantinedPhrase),
            "G.0: quarantined record must not surface via GetForSemanticSearchByTypeAsync");
        byTier.Should().NotContain(c => c.Contains(quarantinedPhrase),
            "G.0: quarantined record must not surface via GetForTierScopedSearchAsync");
    }
}
