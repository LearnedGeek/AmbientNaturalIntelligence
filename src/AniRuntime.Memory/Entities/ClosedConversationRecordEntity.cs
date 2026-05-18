using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AniRuntime.Memory.Entities;

/// <summary>
/// Vibe Loop V1 closed-conversation record — a structured paraphrase of a
/// closed thread including gist, topic keywords, register snapshots for both
/// speakers, outcome signal seed, and a small embedding for retrieval-time
/// biasing. Currently owned by <c>SqliteClosedConversationStore</c>; will
/// migrate to EF in Phase 2.
/// </summary>
[Table("closed_conversation_records")]
public class ClosedConversationRecordEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("thread_id")]
    public Guid ThreadId { get; set; }

    [Column("closed_at")]
    public DateTimeOffset ClosedAt { get; set; }

    [Column("gist")]
    public string Gist { get; set; } = string.Empty;

    [Column("topic_keywords_json")]
    public string TopicKeywordsJson { get; set; } = string.Empty;

    [Column("mark_register_json")]
    public string MarkRegisterJson { get; set; } = string.Empty;

    [Column("ani_register_json")]
    public string AniRegisterJson { get; set; } = string.Empty;

    [Column("outcome_signal_seed_json")]
    public string OutcomeSignalSeedJson { get; set; } = string.Empty;

    [Column("outcome_signal_valence")]
    public float OutcomeSignalValence { get; set; }

    [Column("turn_count")]
    public int TurnCount { get; set; }

    [Column("duration_seconds")]
    public float DurationSeconds { get; set; }

    [Column("embedding")]
    public float[]? Embedding { get; set; }
}
