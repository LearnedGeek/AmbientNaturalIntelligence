using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AniRuntime.Memory.Entities;

/// <summary>
/// Four singleton state-blob tables: character_state, desire_state,
/// emotional_state, relationship_health. All have the same shape —
/// single row with id=1 + JSON-serialized doc. Pre-EF code used raw
/// INSERT OR REPLACE; EF will use SaveChanges with key collision
/// handling.
/// </summary>
[Table("character_state")]
public class CharacterStateEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("json")]
    public string Json { get; set; } = string.Empty;
}

[Table("desire_state")]
public class DesireStateEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("json")]
    public string Json { get; set; } = string.Empty;
}

[Table("emotional_state")]
public class EmotionalStateBlobEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("json")]
    public string Json { get; set; } = string.Empty;
}

[Table("relationship_health")]
public class RelationshipHealthEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("json")]
    public string Json { get; set; } = string.Empty;
}
