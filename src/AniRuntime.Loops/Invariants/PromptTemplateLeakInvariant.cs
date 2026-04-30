using System.Text.RegularExpressions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Theme J Phase J.4 (Apr 30, 2026) — universal prompt-template-leak
/// invariant. Fails when the artifact's content contains directive
/// phrases that look like prompt-template language (e.g. literal
/// *"WHAT IS TRUE about Mark"*) or close paraphrases (e.g. *"so here's
/// what true:"* — the Apr 30 morning empirical instance).
///
/// **Apr 30 morning origin**: 08:28:12 World Experience inner thought
/// contained *"so here's what true: mark has a desk at home with three
/// old books stacked on one corner"*. The phrase paraphrased the
/// directive header at <c>PromptBuilder.cs:518/525/605/612</c>
/// (*"WHAT IS TRUE about {contact}"*) and attached the prompt-borrowed
/// authority phrasing to confabulated content.
///
/// **Universal applicability**: applies to ALL output kinds. Prompt
/// directives are NEVER appropriate in generation — including inner
/// thoughts and world experiences. (This differs from anti-parrot,
/// which is type-conditional.)
///
/// **Detection strategy**:
/// 1. Literal pattern list — exact + close-paraphrase variants of
///    known directive phrases.
/// 2. (Future) regex variants — caught common reorderings.
/// 3. Optional input cross-check — if
///    <see cref="CognitiveArtifact.SystemPromptText"/> is provided, also
///    flag any 5+ token verbatim run from system prompt to output. Not
///    implemented in J.4 (deferred to J.5 since it's substantively
///    different from the literal-pattern check).
/// </summary>
public sealed partial class PromptTemplateLeakInvariant : ICognitiveOutputInvariant
{
    public string Name => "prompt-template-leak";

    public bool AppliesTo(CognitiveArtifact artifact) => true;

    /// <summary>
    /// Known leak patterns — exact substrings AND regex paraphrases.
    /// Case-insensitive matching via <see cref="RegexOptions.IgnoreCase"/>.
    /// Adding patterns here is the supported way to extend coverage as
    /// new leak shapes surface in production.
    /// </summary>
    private static readonly Regex[] LeakPatterns =
    {
        // Apr 30 empirical — "WHAT IS TRUE" directive header
        // (PromptBuilder.cs:518/525/605/612) and its observed paraphrase.
        WhatIsTrueDirective(),
        SoHereIsWhatTrueParaphrase(),

        // Generic prompt-step markers commonly seen in LLM directives.
        StepNumberClassify(),
        OutputOnlyJson(),
        DoNotRepeatInstruction(),

        // Bracketed prompt-section labels (e.g. "[CONTEXT]", "[INSTRUCTIONS]").
        BracketedSectionLabel(),
    };

    public Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct)
    {
        var content = artifact.Content;
        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(InvariantResult.Pass());

        foreach (var pattern in LeakPatterns)
        {
            var match = pattern.Match(content);
            if (match.Success)
            {
                var hint =
                    $"output contains prompt-template directive phrase: \"{Truncate(match.Value, 80)}\" (pattern: {pattern})";
                return Task.FromResult(InvariantResult.Fail(hint));
            }
        }

        return Task.FromResult(InvariantResult.Pass());
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    // ── Pattern definitions (compile-time-generated regex for perf) ──────

    // Broadened Apr 30 17:01 after Mark's regression tag: Ani's 17:00:23
    // reply contained "i don't see anything in WHAT IS TRUE above about
    // tonight's lesson" — the prior regex required "WHAT IS TRUE about"
    // immediately, missed "WHAT IS TRUE above". Both forms are the
    // directive section header leaking. Case-sensitive on purpose: the
    // directive is rendered in ALL CAPS in the prompt; lowercase "what
    // is true" in casual prose is not a leak (e.g. "what is true is...").
    [GeneratedRegex(@"\bWHAT\s+IS\s+TRUE\b")]
    private static partial Regex WhatIsTrueDirective();

    [GeneratedRegex(@"(?:so\s+)?here'?s?\s+what(?:'?s)?\s+true", RegexOptions.IgnoreCase)]
    private static partial Regex SoHereIsWhatTrueParaphrase();

    [GeneratedRegex(@"STEP\s+\d+\s*[—\-:]+\s*(CLASSIFY|EXTRACT|EVALUATE|SCORE|RETURN)", RegexOptions.IgnoreCase)]
    private static partial Regex StepNumberClassify();

    [GeneratedRegex(@"output\s+only\s+json|return\s+only\s+json", RegexOptions.IgnoreCase)]
    private static partial Regex OutputOnlyJson();

    [GeneratedRegex(@"do\s+not\s+(?:repeat|fabricate|invent|hallucinate)", RegexOptions.IgnoreCase)]
    private static partial Regex DoNotRepeatInstruction();

    [GeneratedRegex(@"\[(?:CONTEXT|INSTRUCTIONS?|CRITICAL|IMPORTANT|RULES|STEP\s*\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedSectionLabel();
}
