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
    /// Head-window size (characters) for the contact-name-as-subject
    /// heuristic. Contact names appearing beyond this offset are treated
    /// as passing mentions inside longer sentences rather than the
    /// grammatical subject of a foundation fact.
    /// </summary>
    private const int ContactMatchHeadWindow = 60;

    /// <summary>
    /// Classify the voice of an anchored (foundation) memory. Handles null
    /// and empty content defensively (returns Unclassified) so callers can
    /// invoke unconditionally without defensive checks at the call site.
    ///
    /// <para>
    /// Priority order (first match wins) — reordered PR #117 review-fix so
    /// first-person "I always thought Mark's..." reads as self-statement
    /// rather than being pulled into the contact-fact bucket by the
    /// head-window match:
    /// <list type="number">
    ///   <item><c>Provenance = Interior</c> → <see cref="AnchoredMemoryVoice.AniSelfStatement"/>
    ///         — Interior tier IS Ani's inner life by definition.</item>
    ///   <item>Content starts with second-person "You" / "Your" (apostrophe
    ///         forms hit the same probe via word-boundary) →
    ///         <see cref="AnchoredMemoryVoice.AniSelfStatement"/> —
    ///         character-seed shape addressing Ani.</item>
    ///   <item>Content starts with first-person "I" / "My" (apostrophe
    ///         forms hit the "I" probe via word-boundary) →
    ///         <see cref="AnchoredMemoryVoice.AniSelfStatement"/>.</item>
    ///   <item>When <paramref name="contactName"/> is supplied: contact
    ///         name appears as subject at start OR within the head window
    ///         → <see cref="AnchoredMemoryVoice.MarkFactAssertion"/>.
    ///         Skipped entirely when <paramref name="contactName"/> is
    ///         null — no hardcoded fallback (PR #117 review-fix; the
    ///         Phase 5/6 "never hardcode Mark" discipline).</item>
    ///   <item>Fallback → <see cref="AnchoredMemoryVoice.SeedNarrative"/>
    ///         (background world / atmospheric context).</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="memory">The anchored memory to classify.</param>
    /// <param name="contactName">
    /// The configured primary contact name (e.g. from
    /// <c>CharacterStateDoc.PrimaryContactName</c>). When null, contact-
    /// subject detection is skipped entirely — records that would have
    /// matched fall to <see cref="AnchoredMemoryVoice.SeedNarrative"/>
    /// rather than false-matching a hardcoded name (PR #117 review).
    /// </param>
    public static AnchoredMemoryVoice Classify(MemoryRecord memory, string? contactName = null)
    {
        if (memory is null || string.IsNullOrWhiteSpace(memory.Content))
            return AnchoredMemoryVoice.Unclassified;

        if (memory.Provenance == EpistemicTier.Interior)
            return AnchoredMemoryVoice.AniSelfStatement;

        var trimmed = memory.Content.TrimStart();

        // Second-person opener → Ani-self-statement (character-seed shape).
        // "You're" / "Youre" hit the "You" probe via word-boundary
        // (apostrophe is non-alphanumeric, so it counts as boundary).
        if (StartsWithWord(trimmed, "You") || StartsWithWord(trimmed, "Your"))
            return AnchoredMemoryVoice.AniSelfStatement;

        // First-person opener → self-statement. Reordered ABOVE the
        // contact-name check (PR #117 review — Devin) so "I always
        // thought Mark's shop was warmer" reads as self-statement rather
        // than falling into MarkFactAssertion via the head-window match.
        // "I'm" / "Im" / "I've" hit the "I" probe via word-boundary.
        if (StartsWithWord(trimmed, "I") || StartsWithWord(trimmed, "My"))
            return AnchoredMemoryVoice.AniSelfStatement;

        // Contact-name-as-subject match. Skipped entirely when contactName
        // is null (PR #117 review — no hardcoded "Mark" fallback).
        if (!string.IsNullOrEmpty(contactName))
        {
            if (StartsWithWord(trimmed, contactName))
                return AnchoredMemoryVoice.MarkFactAssertion;

            // Contact-name as a whole word appearing within the head
            // window. Bound the match START index instead of chopping
            // the string (PR #117 review — Devin bug: chopping at 60
            // chars made "Marketplace" starting at offset 56 truncate
            // to "Mark" and false-match as a whole word).
            if (ContainsWordBeforeIndex(trimmed, contactName, ContactMatchHeadWindow))
                return AnchoredMemoryVoice.MarkFactAssertion;
        }

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

    /// <summary>
    /// True if <paramref name="word"/> appears as a whole word in
    /// <paramref name="text"/> STARTING at an index strictly less than
    /// <paramref name="maxStartIndex"/>. Bounds the match start rather
    /// than truncating the string, so a longer word like "Marketplace"
    /// beginning near the window boundary cannot masquerade as the target
    /// via string-end-as-word-boundary (PR #117 review — Devin bug).
    /// </summary>
    private static bool ContainsWordBeforeIndex(string text, string word, int maxStartIndex)
    {
        var searchLimit = Math.Min(text.Length, maxStartIndex);
        var idx = 0;
        while (idx < searchLimit)
        {
            var found = text.IndexOf(word, idx, StringComparison.Ordinal);
            if (found < 0 || found >= searchLimit) return false;
            var before = found == 0 || !(char.IsLetterOrDigit(text[found - 1]) || text[found - 1] == '_');
            var afterIdx = found + word.Length;
            var after = afterIdx >= text.Length || !(char.IsLetterOrDigit(text[afterIdx]) || text[afterIdx] == '_');
            if (before && after) return true;
            idx = found + 1;
        }
        return false;
    }
}
