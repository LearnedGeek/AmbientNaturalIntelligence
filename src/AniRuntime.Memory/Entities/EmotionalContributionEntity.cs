using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AniRuntime.Memory.Entities;

/// <summary>
/// Per-thought decay model emotional contribution. Each cognitive event
/// produces one of these with independent half-life. State = baselines +
/// Σ(decayed contributions).
/// </summary>
[Table("emotional_contributions")]
public class EmotionalContributionEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("source_content")]
    public string SourceContent { get; set; } = string.Empty;

    [Column("warmth_delta")]
    public float WarmthDelta { get; set; }

    [Column("energy_delta")]
    public float EnergyDelta { get; set; }

    [Column("concern_delta")]
    public float ConcernDelta { get; set; }

    [Column("playfulness_delta")]
    public float PlayfulnessDelta { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("half_life_hours")]
    public float HalfLifeHours { get; set; }

    [Column("category")]
    public string Category { get; set; } = string.Empty;

    [Column("embedding")]
    public float[]? Embedding { get; set; }

    [Column("severity")]
    public float Severity { get; set; } = 1.0f;

    [Column("is_outreach_ready")]
    public bool IsOutreachReady { get; set; }

    [Column("register")]
    public string Register { get; set; } = "Wistful";

    [Column("ml_emotion")]
    public string? MLEmotion { get; set; }

    [Column("ml_confidence")]
    public float? MLConfidence { get; set; }

    [Column("ml_sarcasm")]
    public bool? MLSarcasm { get; set; }

    [Column("divergence_score")]
    public float? DivergenceScore { get; set; }

    [Column("associative_anchor")]
    public string? AssociativeAnchor { get; set; }
}
