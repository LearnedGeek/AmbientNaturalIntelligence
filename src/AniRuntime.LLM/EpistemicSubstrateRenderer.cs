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
    // Default cap on records per slice — matches the legacy inline rendering
    // budgets in PromptBuilder (which used Take(6) for facts, Take(3) for
    // world experiences, etc.). Slices that want a different cap can pass
    // a pre-filtered list; the renderer respects whatever it receives.
    private const int FactsRenderCap        = 6;
    private const int AniWorldRenderCap     = 3;
    private const int AniPriorRenderCap     = 5;

    /// <inheritdoc />
    public string RenderActiveThreadSlice(StructuredConversationSummary? summary, string contactName)
    {
        if (summary is null || summary.Turns.Count == 0) return string.Empty;

        var safeContact = SafeContact(contactName);

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

    /// <inheritdoc />
    public string RenderMarkAssertedFactsSlice(IReadOnlyList<MemoryRecord>? facts, string contactName)
    {
        if (facts is null) return string.Empty;
        var rows = facts
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Take(FactsRenderCap)
            .ToList();
        if (rows.Count == 0) return string.Empty;

        var safeContact = SafeContact(contactName);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[FACTS about {safeContact} and the shared world — epistemic framing:");
        sb.AppendLine($" • each line is something {safeContact} has said or that has been observed about him.");
        sb.AppendLine($" • these are the ONLY claims about {safeContact}'s external world you may assert.");
        sb.AppendLine($" • if your reply names a coworker, project, place, or event in {safeContact}'s life,");
        sb.AppendLine($"   that entity MUST appear below; otherwise you don't know it.]");
        foreach (var m in rows)
            sb.AppendLine($"  - {PromptBuilder.FormatMemoryWithTime(m)}");
        return sb.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public string RenderAniWorldSlice(
        string?                       occupation,
        IReadOnlyList<string>?        natureGrounding,
        IReadOnlyList<MemoryRecord>?  recentWorldExperiences)
    {
        var hasOccupation = !string.IsNullOrWhiteSpace(occupation);
        var natureRows = (natureGrounding ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(2)
            .ToList();
        var worldRows = (recentWorldExperiences ?? Array.Empty<MemoryRecord>())
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Take(AniWorldRenderCap)
            .ToList();

        if (!hasOccupation && natureRows.Count == 0 && worldRows.Count == 0)
            return string.Empty;

        // FC-002 three-axis rule's "self-world ALLOW" surface expressed
        // positively. Tells the model the substrate-support requirement
        // does NOT apply to her own interior — she can imagine, dream,
        // expand details about her own life even when novel.
        // Mark's framing principle: "if she makes up something about
        // her own world that is fine."
        //
        // 2026-05-16 Posture S: "bookstore-world life" → "own life" and
        // "canonical occupation" → "current focus". The slice no longer
        // reasserts a frozen occupation frame; substrate is the source of
        // who-she-is, and any non-empty occupation flowing through is
        // treated as a contemporaneous focus rather than a permanent label.
        // See docs/spec/ANI-Substrate-Led-Character-Plan.md §3.2 H2.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ANI-WORLD — your own life — epistemic framing:");
        sb.AppendLine(" • you have latitude here. Your routines, surroundings, imagined scenes,");
        sb.AppendLine("   sensory details about your own world are yours to express.");
        sb.AppendLine(" • the substrate-support requirement that applies to the contact's external");
        sb.AppendLine("   world does NOT apply to your own interior. Stay consistent with what you've");
        sb.AppendLine("   established (recent world entries below) but novelty");
        sb.AppendLine("   within your own world is allowed.]");
        if (hasOccupation)
            sb.AppendLine($"  - current focus: {occupation}");
        foreach (var n in natureRows)
            sb.AppendLine($"  - nature grounding: {n}");
        foreach (var m in worldRows)
            sb.AppendLine($"  - recent world: {PromptBuilder.FormatMemoryWithTime(m)}");
        return sb.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public string RenderAniPriorSlice(IReadOnlyList<MemoryRecord>? priorEpisodic)
    {
        if (priorEpisodic is null) return string.Empty;
        var rows = priorEpisodic
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Take(AniPriorRenderCap)
            .ToList();
        if (rows.Count == 0) return string.Empty;

        // FC-004 epistemic-asymmetry surface for free-form Episodic records.
        // Distinct from RenderActiveThreadSlice (which renders the
        // structured per-speaker summary): this slice handles Episodic
        // records that surface via semantic search and would otherwise leak
        // into composition prompts as if they were established context.
        // The May 12 23:23 windshield-as-fact case is the empirical anchor.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ANI-PRIOR — your own prior conversational output — epistemic framing:");
        sb.AppendLine(" • these are things YOU said earlier, surfaced for continuity awareness.");
        sb.AppendLine(" • they are your prior turns, NOT yet established as fact.");
        sb.AppendLine(" • do NOT reason from them as if the contact had verified them.]");
        foreach (var m in rows)
            sb.AppendLine($"  - {PromptBuilder.FormatMemoryWithTime(m)}");
        return sb.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public string RenderClosedConversationSlice(ClosedConversationRecord? closed, string contactName)
    {
        if (closed is null || string.IsNullOrWhiteSpace(closed.Gist)) return string.Empty;

        var safeContact = SafeContact(contactName);

        // Closed-conversation gists are Vibe Loop V1 paraphrased outputs —
        // safe to surface as callback ground because they're structurally
        // no-verbatim. FC-011 (substrate-supported callbacks, deferred)
        // is the consumer that activates this slice once Vibe Loop V1.5
        // ships. The slice exists now so producers can be migrated ahead
        // of the V1.5 dependency landing.
        return
            $"[CLOSED-CONVERSATION with {safeContact} — epistemic framing:\n" +
            $" • paraphrased gist of a recent prior conversation you and {safeContact} had.\n" +
            $" • this is established callback ground — you and {safeContact} both lived this,\n" +
            $"   so referencing it (\"that thing we talked about\") is appropriate.\n" +
            $" • do NOT lift verbatim phrasing — the gist is the substrate, not a script.]\n" +
            $"  - {closed.Gist}";
    }

    /// <inheritdoc />
    public string RenderThreeAxisRuleSlice(string contactName)
    {
        var safeContact = SafeContact(contactName);

        // FC-006 three-axis rule. The verifier prompt (and any other
        // consumer that needs to encode the rule) reads from this single
        // source of truth so the rule's canonical wording lives in one
        // place. Subject × Modality × Substrate — see FCR § FC-002 / FC-006.
        return
            $"[THREE-AXIS CLAIM RULE — apply to every claim in the candidate output:\n" +
            $"\n" +
            $"  SUBJECT axis — who/what does the claim describe?\n" +
            $"    • SELF-WORLD: the speaker's own life (Ani's bookstore world).\n" +
            $"    • SHARED: Ani + {safeContact} together (joint actions, joint possessions, joint memories).\n" +
            $"    • {safeContact.ToUpperInvariant()}-WORLD: {safeContact}'s life (his job, his home, his people).\n" +
            $"\n" +
            $"  MODALITY axis — how is the claim framed?\n" +
            $"    • FACTUAL: stated as something that IS or HAS happened (\"I have X\", \"we did Y\").\n" +
            $"    • MODAL: framed as imagining / wishing / dreaming / wondering / thinking-about.\n" +
            $"\n" +
            $"  SUBSTRATE axis — does the claim trace to grounded substrate?\n" +
            $"    • SUPPORTED: traceable to {safeContact}'s text, prior conversation, world layer, or character seeds.\n" +
            $"    • NOVEL: not present in substrate.\n" +
            $"\n" +
            $"  RULE: factual ⇒ (self-world OR substrate-supported). Modal claims always allowed.\n" +
            $"\n" +
            $"  WORKED VERDICTS:\n" +
            $"    • SELF / factual / novel  → ALLOW   (she has latitude on her own life)\n" +
            $"    • SHARED / factual / novel → BLOCK  (joint claims need substrate)\n" +
            $"    • SHARED / modal  / novel → ALLOW   (modal framing always passes)\n" +
            $"    • {safeContact.ToUpperInvariant()}-WORLD / factual / novel → BLOCK (his-world claims need substrate)\n" +
            $"    • SHARED / factual / supported → ALLOW (callback case)]";
    }

    /// <inheritdoc />
    public string RenderReplySpeechActDisciplineSlice(string contactName)
    {
        var safeContact = SafeContact(contactName);

        // FC-005 reply-side speech-act discipline. Distinct from entity
        // discipline (covered in the CRITICAL block / Mark-asserted facts
        // slice): this is about past-turn attribution. The model must not
        // invent past statements attributed to {contact}.
        return
            $"[SPEECH-ACT ATTRIBUTION — discipline for your reply:\n" +
            $" • if your reply uses past-turn attribution language — \"you mentioned\",\n" +
            $"   \"we talked about\", \"you told me\", \"you said\", \"remember when you said\" —\n" +
            $"   that statement MUST trace to actual conversation history or a [FACTS] record.\n" +
            $" • do NOT invent past speech acts. If {safeContact} hasn't said it (in conversation\n" +
            $"   history or [FACTS]), do not attribute it to him.\n" +
            $" • this is source attribution: the reference must have a verifiable source.]";
    }

    private static string SafeContact(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "the contact" : name;
}
