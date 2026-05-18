using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AniRuntime.Memory.Entities;

/// <summary>
/// Conversation thread record — owns the lifecycle of a back-and-forth
/// exchange (started_at, last_message_at, is_active, initiated_by).
/// Currently owned by <c>SqliteConversationService</c>; will migrate to
/// EF in Phase 2.
/// </summary>
[Table("conversation_threads")]
public class ConversationThreadEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [Column("last_message_at")]
    public DateTimeOffset LastMessageAt { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("initiated_by")]
    public string InitiatedBy { get; set; } = "mark";
}

/// <summary>
/// Conversation message record — one row per inbound/outbound message
/// within a thread. FK to conversation_threads(id).
/// </summary>
[Table("conversation_messages")]
public class ConversationMessageEntity
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("thread_id")]
    public Guid ThreadId { get; set; }

    [Column("role")]
    public string Role { get; set; } = string.Empty;

    [Column("content")]
    public string Content { get; set; } = string.Empty;

    [Column("sent_at")]
    public DateTimeOffset SentAt { get; set; }
}
