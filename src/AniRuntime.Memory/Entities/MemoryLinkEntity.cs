using System.ComponentModel.DataAnnotations.Schema;

namespace AniRuntime.Memory.Entities;

/// <summary>
/// EF Core entity for the <c>memory_links</c> table — Feature 31 A-MEM
/// linked memory graph. Composite primary key on (SourceId, TargetId,
/// Relationship). FK constraints to memories(id) on both source and target.
/// Configured in <see cref="AniDbContext.OnModelCreating"/>.
/// </summary>
[Table("memory_links")]
public class MemoryLinkEntity
{
    [Column("source_id")]
    public Guid SourceId { get; set; }

    [Column("target_id")]
    public Guid TargetId { get; set; }

    [Column("relationship")]
    public string Relationship { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}
