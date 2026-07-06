using System.Text.RegularExpressions;
using AniRuntime.Core.Utilities;

namespace AniRuntime.Loops.Coreference;

/// <summary>
/// **STOP-GAP** (May 6, 2026 — to be replaced shortly). Producer-side
/// deterministic in-place rewriter for direct-address coreference
/// failures. Bridges the gap between the prior LLM-based pronoun-fix
/// (retired May 6 — leaked working text twice on May 5) and the proper
/// ML-based coreference resolution scoped at
/// <c>docs/research/model-coreference-ideas.md</c> (Maverick / F-Coref
/// via LM-Kit, parallel to the emotional detection model).
///
/// Mark's framing on the regex approach: *"a regex is a terrible
/// solution. we rely on it far too much"* — accepted as a stop-gap so
/// the May 5 leak shape doesn't repeat in the meantime, but on the
/// understanding that the proper coref model lands soon and this file
/// is retired with it. Do not extend this file further. New cases
/// missed by it should be flagged on the gate's
/// <c>DirectAddressInvariant</c> (which catches what the rewriter
/// misses) and queued for the coref-model replacement, not patched
/// here.
///
/// **Pipeline placement**: runs in producers (OutreachPhase /
/// ConversationReplyPhase) BEFORE the universal gate. The gate's
/// <c>DirectAddressInvariant</c> is the safety net for whatever this
/// rewriter misses. If the gate still fires after the rewriter ran, the
/// existing per-pipeline verdict behaviour applies (Outreach drops the
/// message; ConversationReply remediates via regen).
///
/// **Substitutions** (post K.4c 2026-07-06 pronoun-swap retirement):
/// <list type="bullet">
///   <item><c>{ContactName}</c> → <c>you</c></item>
///   <item><c>{ContactName}'s</c> → <c>your</c></item>
/// </list>
///
/// **What was removed on 2026-07-06 and why.** Prior versions also swapped
/// <c>he/him/his</c> → <c>you/your</c> blindly. Empirical anchor:
/// 2026-07-06 13:28 CDT — Ani composed *"bob ross would've loved this
/// sky. he'd have dipped his brush in titanium white..."* and the
/// rewriter transformed it to *"you'd have dipped your brush"*, breaking
/// a legitimate third-party reference into nonsense addressed at Mark.
/// Follow-up test-harness run (7 scenarios probing third-party pronoun
/// attribution on <c>ani-v7-conversation</c> + Modelfile SYSTEM + no
/// substrate) showed the fine-tune handles third-party <c>he/she/they</c>
/// pronouns correctly on its own — the rewriter's pronoun-swap was
/// causing more harm than help. Kept: proper-noun swap, which the
/// harness confirmed IS still needed — the model occasionally refers to
/// Mark by name in third person ("i've been stealing from mark's desk"
/// when Mark IS the addressee). That specific failure the rewriter
/// still catches.
///
/// **Verb-agreement post-pass** on the resulting <c>you {VERB}</c>:
/// <list type="bullet">
///   <item>Irregular table: <c>is/was/has/does/goes</c> → <c>are/were/have/do/go</c></item>
///   <item>Adverb-tolerant: <c>you always cracks</c> → <c>you always crack</c></item>
///   <item>Regular <c>-s</c> drop: <c>thinks</c> → <c>think</c></item>
///   <item><c>-ies</c> → <c>-y</c>: <c>tries</c> → <c>try</c></item>
///   <item>Sibilant <c>-es</c> drop: <c>watches</c> → <c>watch</c></item>
///   <item>Skip list (plural-noun forms after "you"): <c>guys/all/two/both/ladies/kids</c></item>
/// </list>
///
/// Idempotent. Word-boundary safe (<c>\b</c> anchors prevent
/// "she"→"syou" or "remarkable"→"reyouable").
/// </summary>
public static class DirectAddressRewriter
{
    private static readonly Dictionary<string, string> IrregularConjugations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "is",   "are"  },
            { "was",  "were" },
            { "has",  "have" },
            { "does", "do"   },
            { "goes", "go"   },
        };

    private static readonly HashSet<string> NonVerbAfterYou =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "guys", "all", "two", "both", "ladies", "kids", "lot",
        };

    private const string AdverbAlternation =
        "always|never|often|sometimes|usually|just|really|still|already|" +
        "barely|hardly|rarely|seldom|also|only|definitely|probably|" +
        "certainly|actually|totally|completely|absolutely|literally";

    // K.4c (2026-07-06) — HisRegex / HimRegex / HeRegex removed. The
    // fine-tune handles third-party he/him/his correctly on its own; the
    // blind swap was over-firing on legitimate third-party references
    // (Bob Ross case). See class-level docs for the empirical anchor and
    // harness verification.

    private static readonly Regex YouMaybeAdverbsVerbRegex =
        new($@"\byou(?:\s+(?:{AdverbAlternation}))*\s+(\w+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Rewrite(string content, string contactName)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var result = content;

        if (!string.IsNullOrWhiteSpace(contactName))
        {
            result = SafeRegex.Replace(
                result,
                $@"\b{Regex.Escape(contactName)}'s\b",
                "your",
                RegexOptions.IgnoreCase);
        }

        // K.4c (2026-07-06) — pronoun-swap block removed. The fine-tune
        // handles third-party he/him/his correctly. Verified by the
        // 2026-07-06 harness against ani-v7-conversation with Modelfile
        // SYSTEM only — 4/4 clear third-party scenarios passed with
        // correct pronouns.

        if (!string.IsNullOrWhiteSpace(contactName))
        {
            result = SafeRegex.Replace(
                result,
                $@"\b{Regex.Escape(contactName)}\b",
                "you",
                RegexOptions.IgnoreCase);
        }

        result = YouMaybeAdverbsVerbRegex.Replace(
            result, m => ConjugateMatch(m, m.Groups[1].Value));

        return result;
    }

    private static string ConjugateMatch(Match m, string verb)
    {
        if (NonVerbAfterYou.Contains(verb))
            return m.Value;

        if (IrregularConjugations.TryGetValue(verb, out var irreg))
            return m.Value.Substring(0, m.Value.Length - verb.Length) + irreg;

        var conjugated = ConjugateRegular(verb);
        if (conjugated == verb)
            return m.Value;
        return m.Value.Substring(0, m.Value.Length - verb.Length) + conjugated;
    }

    private static string ConjugateRegular(string verb)
    {
        if (!verb.EndsWith("s", StringComparison.OrdinalIgnoreCase) || verb.Length <= 2)
            return verb;

        if (verb.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            return verb;

        if (verb.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && verb.Length > 3)
            return verb.Substring(0, verb.Length - 3) + "y";

        if (verb.Length > 3 &&
            (verb.EndsWith("ses", StringComparison.OrdinalIgnoreCase) ||
             verb.EndsWith("xes", StringComparison.OrdinalIgnoreCase) ||
             verb.EndsWith("ches", StringComparison.OrdinalIgnoreCase) ||
             verb.EndsWith("shes", StringComparison.OrdinalIgnoreCase)))
            return verb.Substring(0, verb.Length - 2);

        return verb.Substring(0, verb.Length - 1);
    }
}
