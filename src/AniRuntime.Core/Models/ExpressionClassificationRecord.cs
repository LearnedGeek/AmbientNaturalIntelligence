namespace AniRuntime.Core.Models;

/// <summary>
/// Issue #41 (Theme I prerequisite, 2026-05-21) — one per-turn classification
/// record produced by the LM-Kit classifier surface.
///
/// **Two source types share this table** (one-data-plane principle):
///
/// 1. <c>runtime</c> — emitted during normal conversation cycles when the
///    closed-conversation summarizer or per-turn classifier runs. FK'd into
///    <c>conversation_threads</c> + <c>conversation_messages</c> by id.
///    Carries both <see cref="FeltRegister"/> (9-register, from
///    <c>ClassifyRegisterAsync</c>) and optionally
///    <see cref="ExpressedEmotion"/> (LM-Kit 6-class, from
///    <c>ClassifyEmotionAsync</c>).
///
/// 2. <c>batch_dashboard</c> — emitted by the Research.razor batch classify
///    button over external corpus files (Grok exports, JSONL fixtures in
///    <c>docs/conversations/classify/</c>). No FK to ANI's own threads; anchors
///    to <see cref="SourceFile"/> + <see cref="SourceIndex"/> instead.
///    Typically carries only <see cref="ExpressedEmotion"/>; <see cref="FeltRegister"/>
///    may be null unless the batch path also runs the register classifier.
///
/// **Why a single table.** Cramér's V over (felt, expressed) is computable
/// from any rows where both columns are populated, regardless of source.
/// Per-conversation drill-down filters on source = 'runtime' + thread_id.
/// External-corpus comparisons filter on source = 'batch_dashboard'. The
/// dashboard heatmap and paper-figure work share one substrate.
///
/// Schema migration follows the validity-column pattern from J.5h
/// (idempotent <c>CREATE TABLE IF NOT EXISTS</c> + <c>ALTER TABLE</c> retro-add).
/// </summary>
public sealed class ExpressionClassificationRecord
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// <c>"runtime"</c> for in-cycle classifications; <c>"batch_dashboard"</c>
    /// for Research.razor batch processing of external corpus.
    /// </summary>
    public required string Source { get; set; }

    /// <summary>FK to <c>conversation_threads.id</c>. Null for batch source.</summary>
    public Guid? ThreadId { get; set; }

    /// <summary>FK to <c>conversation_messages.id</c>. Null for batch source.</summary>
    public long? MessageId { get; set; }

    /// <summary>Batch source: which file. Null for runtime source.</summary>
    public string? SourceFile { get; set; }

    /// <summary>Batch source: 0-indexed turn position within the file. Null for runtime source.</summary>
    public int? SourceIndex { get; set; }

    /// <summary>Role of the classified turn — <c>"mark"</c> / <c>"ani"</c> (runtime) or <c>"user"</c> / <c>"response"</c> (Grok corpus).</summary>
    public string? Role { get; set; }

    /// <summary>SHA256 hash of the classified text. Audit + dedup.</summary>
    public string? ContentHash { get; set; }

    /// <summary>9-register classification from <c>ClassifyRegisterAsync</c>. Nullable — batch path may skip.</summary>
    public string? FeltRegister { get; set; }

    /// <summary>LM-Kit 6-class emotion from <c>ClassifyEmotionAsync</c>. Nullable — runtime path may skip.</summary>
    public string? ExpressedEmotion { get; set; }

    /// <summary>Classifier confidence in [0, 1]. Nullable — not all paths emit confidence.</summary>
    public float? Confidence { get; set; }

    /// <summary>UTC timestamp of classification.</summary>
    public required DateTimeOffset ClassifiedAt { get; set; }
}
