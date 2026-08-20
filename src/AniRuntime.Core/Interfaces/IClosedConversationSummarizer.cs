using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Vibe Loop V1.2 (Apr 29, 2026) — produces a structured
/// <see cref="ClosedConversationRecord"/> from a closed
/// <see cref="ConversationThread"/>.
///
/// Three concerns delivered in one pass:
///  1. <b>Anti-parrot gist</b>: an LLM-generated 1-2 sentence paraphrase
///     of the thread, with the prompt structurally forbidding verbatim
///     quotation of contact turns. Replaces the pre-V1 verbatim-prose
///     <c>Conversation (N messages):</c> Episodic record that leaked
///     Mark's own text into outreach composition.
///  2. <b>Per-speaker 9-register vectors</b>: each speaker's prevalence
///     across the canonical 9 registers (Tenderness / Longing /
///     Playfulness / Curiosity / Desire / Existential / Wistful /
///     Frustration / Delight). Lerman territory for Paper 2 — finer
///     grained than Chu et al. 2025's aggregate-scale data.
///  3. <b>Outcome-signal seed</b>: Ani's register-vector delta from the
///     first half of her turns to the second half (vector form), plus a
///     scalar valence projection in [-1, +1] (Mark's "anger to happiness
///     sliding scale" framing from V1.0 design alignment).
///
/// Narrow interface per Mar 19 ISP discipline — V1.2 ships exactly the
/// produce-the-record method. Reads of the resulting record live on
/// <see cref="IClosedConversationStore"/>.
/// </summary>
public interface IClosedConversationSummarizer
{
    /// <summary>
    /// Summarise a closed conversation thread into a structured
    /// <see cref="ClosedConversationRecord"/>, wrapped in an
    /// <see cref="IClosedConversationEnvelope"/> for F-1 producer-boundary
    /// provenance (Phase 8c, 2026-08-19). The summarizer does NOT
    /// persist the result — callers (V1.3 <c>CloseThreadAsync</c>)
    /// unwrap via <see cref="IProvenancedContent{T}.Content"/> and hand
    /// the record to <see cref="IClosedConversationStore.SaveAsync"/>.
    ///
    /// The thread MUST contain at least one message; otherwise behaviour
    /// is undefined (an empty thread is not a real "closed conversation"
    /// and should not be reaching this surface).
    /// </summary>
    Task<IClosedConversationEnvelope> SummariseAsync(
        ConversationThread thread, CancellationToken ct = default);
}
