using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Repositories;

public sealed class MemoryLinkRepository : IMemoryLinkRepository
{
    private readonly AniDbContext _db;

    public MemoryLinkRepository(AniDbContext db) => _db = db;

    public void Add(MemoryLinkEntity link) => _db.MemoryLinks.Add(link);

    public async Task<IReadOnlyList<MemoryLinkEntity>> GetLinksForMemoryAsync(
        Guid memoryId, string? relationshipType, CancellationToken ct = default)
    {
        var q = _db.MemoryLinks.AsQueryable()
            .Where(l => l.SourceId == memoryId || l.TargetId == memoryId);

        if (!string.IsNullOrWhiteSpace(relationshipType))
            q = q.Where(l => l.Relationship == relationshipType);

        return await q.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryLinkEntity>> GetAllAsync(CancellationToken ct = default)
        => await _db.MemoryLinks.ToListAsync(ct);

    public Task<int> GetCountAsync(CancellationToken ct = default)
        => _db.MemoryLinks.CountAsync(ct);

    public async Task<int> AddManyAsync(IEnumerable<MemoryLinkEntity> links, CancellationToken ct = default)
    {
        var list = links.ToList();
        if (list.Count == 0) return 0;

        // For duplicate-tolerance, we check existing links and only add the new ones.
        // EF Core doesn't have a native INSERT OR IGNORE in LINQ; this pre-filter
        // pattern is cleaner than catching unique-constraint violations.
        var keys = list.Select(l => new { l.SourceId, l.TargetId, l.Relationship }).ToList();
        var existing = await _db.MemoryLinks
            .Where(l => list.Select(x => x.SourceId).Contains(l.SourceId)
                     && list.Select(x => x.TargetId).Contains(l.TargetId))
            .Select(l => new { l.SourceId, l.TargetId, l.Relationship })
            .ToListAsync(ct);

        var existingSet = existing.ToHashSet();
        var toAdd = list
            .Where(l => !existingSet.Contains(new { l.SourceId, l.TargetId, l.Relationship }))
            .ToList();

        if (toAdd.Count == 0) return 0;
        _db.MemoryLinks.AddRange(toAdd);
        return toAdd.Count;
    }
}
