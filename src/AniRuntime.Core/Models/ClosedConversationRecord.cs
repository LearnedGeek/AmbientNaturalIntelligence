namespace AniRuntime.Core.Models;

/// <summary>
/// Vibe Loop V1 (Apr 29, 2026) — structured record produced when a
/// conversation thread closes. Replaces the pre-V1 verbatim-prose Episodic
/// "Conversation (N messages):" record that leaked Mark's verbatim text into
/// outreach composition (Apr 29 verbatim-parrot recurrence).
///
/// Three downstream consumers, one record:
///   1. Outreach prompt composition reads <see cref="Gist"/> as paraphrased
///      relational context — no verbatim transcript, no parrot risk.
///   2. Vibe Loop retrieval-bias (V1.5) reads
///      <see cref="OutcomeSignalValence"/> + <see cref="MarkRegister"/> /
///      <see cref="AniRegister"/> to surface strategies that produced
///      positive outcomes for similar prior states.
///   3. Future Theme J consumers (J.5 producer migration) get a structured
///      surface to consume rather than parsing prose.
///
/// Per-thread fidelity stays in <c>conversation_messages</c> (verbatim);
/// this record is the gist surface. Two surfaces, two purposes — the
/// substrate-typing pattern.
/// </summary>
public class ClosedConversationRecord
{
    /// <summary>Stable identity for retrieval references.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>FK to <c>conversation_threads.id</c>.</summary>
    public Guid ThreadId { get; set; }

    /// <summary>When the thread closed (and this record was written).</summary>
    public DateTimeOffset ClosedAt { get; set; }

    /// <summary>
    /// LMKit-generated 1-2 sentence paraphrase of the conversation. The
    /// summarizer prompt is constrained to NOT lift verbatim quotes from
    /// either speaker — anti-parrot guarantee at the substrate level, not
    /// the consumer level.
    /// </summary>
    public string Gist { get; set; } = string.Empty;

    /// <summary>
    /// LMKit KeywordExtractor TF-IDF output. JSON-serialised list of
    /// topic-bearing keywords for cosine-search-friendly retrieval.
    /// </summary>
    public List<string> TopicKeywords { get; set; } = new();

    /// <summary>
    /// Mark's per-speaker 9-register vector aggregated over his turns in
    /// the thread (mean of LMKit emotion classifications). Lerman territory
    /// for Paper 2 — finer-grained than Chu et al. 2025's aggregate-scale
    /// register similarity. JSON-serialised dict keyed by register name.
    /// </summary>
    public Dictionary<string, float> MarkRegister { get; set; } = new();

    /// <summary>
    /// Ani's per-speaker 9-register vector aggregated over her turns in
    /// the thread.
    /// </summary>
    public Dictionary<string, float> AniRegister { get; set; } = new();

    /// <summary>
    /// 9-dim register-vector delta from start of thread to end of thread
    /// (computed on Ani's vector by default; preserves directionality per
    /// register so finer-grained queries are possible — e.g.,
    /// "find threads where Playfulness rose").
    /// </summary>
    public Dictionary<string, float> OutcomeSignalSeedVector { get; set; } = new();

    /// <summary>
    /// Scalar valence projection of <see cref="OutcomeSignalSeedVector"/>,
    /// range [-1.0, +1.0]. Mark's "anger to happiness sliding scale"
    /// framing (Apr 29 V1.0 design alignment). Positive registers
    /// {Tenderness, Playfulness, Delight, Curiosity} minus negative
    /// {Longing, Frustration, Wistful, Existential, Hurt}, normalised.
    /// Primary sort key for V1.5 retrieval biasing.
    /// </summary>
    public float OutcomeSignalValence { get; set; }

    /// <summary>Number of messages in the original thread.</summary>
    public int TurnCount { get; set; }

    /// <summary>Total elapsed time of the thread, seconds.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    /// Embedding of <see cref="Gist"/> for cosine-similarity retrieval.
    /// Populated by the V1.2 summarizer pipeline (re-embed via
    /// IOllamaClient.EmbedAsync).
    /// </summary>
    public float[]? Embedding { get; set; }
}
