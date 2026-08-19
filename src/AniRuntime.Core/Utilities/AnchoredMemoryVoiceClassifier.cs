using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Core.Utilities;

/// <summary>
/// F-1 Phase 7 (2026-08-19) — heuristic voice classifier for anchored
/// (foundation) memories. Derives <see cref="AnchoredMemoryVoice"/> from
/// <see cref="MemoryRecord.Provenance"/> and pronoun signals in
/// <see cref="MemoryRecord.Content"/>. No LLM call, no schema change,
/// no backfill required — matches the F-1 Phase 7 "minimum data-model
/// change" discipline.
///
/// <para>
/// Lives in <c>AniRuntime.Core</c> so <see cref="MemoryRecord"/> can call
/// it from its explicit-interface <c>IAnchoredMemoryEnvelope.Voice</c>
/// implementation without pulling in a downstream (LLM/prompt-shaping)
/// dependency. If empirical results demand higher-fidelity classification,
/// Phase 7b can replace this heuristic with an LLM-backed
/// <c>IAnchoredMemoryVoiceClassifier</c> interface + implementation
/// (mirror of <c>IRegisterClassifier</c> / <c>IThoughtShapeClassifier</c>).
/// </para>
/// </summary>
public static class AnchoredMemoryVoiceClassifier
{
    /// <summary>
    /// Classify the voice of an anchored (foundation) memory. Handles null
    /// and empty content defensively (returns Unclassified) so callers can
    /// invoke unconditionally without defensive checks at the call site.
    ///
    /// <para>
    /// Priority order (first match wins):
    /// <list type="number">
    ///   <item><c>Provenance = Interior</c> → <see cref="AnchoredMemoryVoice.AniSelfStatement"/>
    ///         — Interior tier IS Ani's inner life by definition.</item>
    ///   <item>Content starts with second-person "You" / "Your" / "You're"
    ///         → <see cref="AnchoredMemoryVoice.AniSelfStatement"/>
    ///         — character-seed shape addressing Ani.</item>
    ///   <item>Content contains "Mark" as subject (first token, or after
    ///         common openers like "That"/"Because") →
    ///         <see cref="AnchoredMemoryVoice.MarkFactAssertion"/>.</item>
    ///   <item>Content starts with first-person "I" / "I'm" / "My" →
    ///         <see cref="AnchoredMemoryVoice.AniSelfStatement"/>
    ///         (uncommon for character seeds but present in some anchored
    ///         inner-thoughts).</item>
    ///   <item>Fallback → <see cref="AnchoredMemoryVoice.SeedNarrative"/>
    ///         (background world / atmospheric context).</item>
    /// </list>
    /// </para>
    /// </summary>
    public static AnchoredMemoryVoice Classify(MemoryRecord memory)
    {
        if (memory is null || string.IsNullOrWhiteSpace(memory.Content))
            return AnchoredMemoryVoice.Unclassified;

        if (memory.Provenance == EpistemicTier.Interior)
            return AnchoredMemoryVoice.AniSelfStatement;

        var trimmed = memory.Content.TrimStart();

        // Second-person opener → Ani-self-statement (character-seed shape).
        // Match on token boundary to avoid catching "Youthful" etc.
        if (StartsWithWord(trimmed, "You")
         || StartsWithWord(trimmed, "Your")
         || StartsWithWord(trimmed, "You're")
         || StartsWithWord(trimmed, "Youre"))
            return AnchoredMemoryVoice.AniSelfStatement;

        // Mark as grammatical subject. Match at start OR after a common
        // sentence-opener that precedes the subject.
        if (StartsWithWord(trimmed, "Mark"))
            return AnchoredMemoryVoice.MarkFactAssertion;

        // "Mark" appearing prominently early in the content (within first
        // ~40 chars) as a word — covers "That's Mark's dad's name" and
        // similar shapes that would otherwise fall through.
        var head = trimmed.Length <= 60 ? trimmed : trimmed[..60];
        if (ContainsWord(head, "Mark"))
            return AnchoredMemoryVoice.MarkFactAssertion;

        // First-person self-reference at start.
        if (StartsWithWord(trimmed, "I")
         || StartsWithWord(trimmed, "I'm")
         || StartsWithWord(trimmed, "Im")
         || StartsWithWord(trimmed, "My"))
            return AnchoredMemoryVoice.AniSelfStatement;

        return AnchoredMemoryVoice.SeedNarrative;
    }

    private static bool StartsWithWord(string text, string word)
    {
        if (text.Length < word.Length) return false;
        if (!text.StartsWith(word, StringComparison.Ordinal)) return false;
        if (text.Length == word.Length) return true;  // exact match
        var next = text[word.Length];
        return !char.IsLetterOrDigit(next) && next != '_';
    }

    private static bool ContainsWord(string text, string word)
    {
        var idx = 0;
        while (idx < text.Length)
        {
            var found = text.IndexOf(word, idx, StringComparison.Ordinal);
            if (found < 0) return false;
            var before = found == 0 || !(char.IsLetterOrDigit(text[found - 1]) || text[found - 1] == '_');
            var afterIdx = found + word.Length;
            var after = afterIdx >= text.Length || !(char.IsLetterOrDigit(text[afterIdx]) || text[afterIdx] == '_');
            if (before && after) return true;
            idx = found + 1;
        }
        return false;
    }
}
