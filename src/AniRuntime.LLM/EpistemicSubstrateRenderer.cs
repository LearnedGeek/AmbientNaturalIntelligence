using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM;

/// <summary>
/// Default <see cref="IEpistemicSubstrateRenderer"/> implementation. Renders
/// substrate slices into prompt text with explicit epistemic-asymmetry
/// framing. Each method has ONE responsibility (SRP) and is unit-testable in
/// isolation — feed records in, assert text out, no <see cref="PromptBuilder"/>
/// round-trip required.
///
/// See <see cref="IEpistemicSubstrateRenderer"/> for the rationale (anti-
/// pattern citation, SOLID alignment, distinction from
/// <see cref="IConsciousSubstrateGist"/>).
///
/// **Stateless.** No fields, no injected dependencies. Safe to register as
/// singleton.
/// </summary>
public sealed class EpistemicSubstrateRenderer : IEpistemicSubstrateRenderer
{
    /// <inheritdoc />
    public string RenderActiveThreadSlice(StructuredConversationSummary? summary, string contactName)
    {
        if (summary is null || summary.Turns.Count == 0) return string.Empty;

        var safeContact = string.IsNullOrWhiteSpace(contactName) ? "the contact" : contactName;

        // The framing block is the FC-004 epistemic-asymmetry fix in slice
        // form. The model is told explicitly which lines are established
        // (Mark-asserted) and which are her own prior conversational
        // output (NOT yet established). This addresses the May 12 23:23
        // production case where the model treated "the note on her
        // windshield" — Ani's own prior outreach content — as established
        // context for a subsequent decision.
        return
            $"[RECENT-THREAD with {safeContact} — epistemic framing:\n" +
            $" • lines labeled \"{safeContact}\" are HIS assertions (treat as established).\n" +
            $" • lines labeled \"Ani\" are YOUR own prior conversational output\n" +
            $"   (your earlier turns, NOT yet established as fact — do NOT reason\n" +
            $"   from them as if they were verified by {safeContact}).]\n" +
            summary.ToPromptString();
    }
}
