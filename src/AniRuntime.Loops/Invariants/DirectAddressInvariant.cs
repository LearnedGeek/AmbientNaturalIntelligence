using System.Text.RegularExpressions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Theme M / Coref-related invariant (May 6, 2026) — direct-address
/// invariant. Fails when a contact-facing output (ConversationReply /
/// Outreach / Voice) references the addressee in third person rather
/// than second person.
///
/// **The architectural rule:** a message addressed TO Mark must use
/// "you" / "your" for Mark, not "he" / "him" / "his" / "Mark". Direct
/// address is a structural property of the output kind, not a stylistic
/// preference. Third-person reference to the addressee is a
/// coref/attribution failure caught at the gate boundary.
///
/// **Why this invariant exists at all:** the prior architecture relied on
/// post-hoc LLM-based pronoun-fix (<c>OutreachPhase.FixPronounsIfNeeded</c>)
/// that invoked Ollama with a "Return ONLY the fixed message text" system
/// prompt. The model didn't always follow that instruction — May 5 leaks
/// at 09:55 + 12:16 emitted both the corrected message AND the model's
/// self-explanation ("fixed message with swapped pronouns:" /
/// "i swapped the pronouns so mark would..."). Adding patterns to
/// <c>MessageCleaner</c> to strip the leaked headers was symptom-treating;
/// the root cause was that an LLM call was in the post-hoc fix path at
/// all, which inherits the LLM's tendency to over-explain its own work.
/// The architectural fix is to detect the failure at the gate (this
/// invariant) and regenerate via the existing remediation path, rather
/// than post-hoc string manipulation. No LLM call in the fix path → no
/// working-text leak surface.
///
/// **Detection strategy** (heuristic V1; full coreference resolution via
/// Maverick / F-Coref through LM-Kit is the future expansion documented
/// in <c>docs/research/model-coreference-ideas.md</c>):
/// - Word-boundary regex match on third-person pronouns: `he`, `him`,
///   `his` (case-insensitive). Word-boundary matching prevents false
///   positives on "she", "history", "this" etc.
/// - Word-boundary regex match on the contact's canonical name as a
///   token. The contact's NAME appearing as subject ("Mark thinks") or
///   object ("about Mark") is a third-person reference.
/// - Possessive forms ("Mark's") also flagged as the possessive case.
///
/// **Pet-name vocatives are NOT flagged.** "hey baby", "hi love", "hey
/// there" — these are second-person addresses using endearments, not
/// third-person references. The invariant doesn't match these because
/// they don't contain "he/him/his/{ContactName}" as words.
///
/// **Type-conditional applicability**: ConversationReply / Outreach /
/// Voice. NOT InnerThought / Reflection / WorldExperience — those are
/// monologue-shape outputs where Ani thinking about Mark in third person
/// is the expected register, not a violation.
///
/// **Future expansion to full coreference resolution.** This invariant
/// catches the dominant failure mode (third-person reference to addressee
/// in direct-address output) using a cheap word-boundary regex check.
/// The broader coreference problem — multi-entity ambiguity, pronoun
/// resolution to non-addressee entities, mixed-sentence binding — is
/// scoped as a future ML-based addition (Maverick / F-Coref via LM-Kit
/// per <c>docs/research/model-coreference-ideas.md</c>). Adding that
/// extension is additive: a new <c>CoreferenceInvariant</c> for full
/// entity tracking, alongside this simpler direct-address check.
/// </summary>
public sealed class DirectAddressInvariant : ICognitiveOutputInvariant
{
    private readonly ILogger<DirectAddressInvariant> _log;

    public string Name => "direct-address";

    /// <summary>
    /// Word-boundary patterns for third-person pronouns. Case-insensitive.
    /// Pre-compiled for hot-path performance.
    /// </summary>
    private static readonly Regex HeProRegex = new(
        @"\bhe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HimProRegex = new(
        @"\bhim\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HisProRegex = new(
        @"\bhis\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DirectAddressInvariant(ILogger<DirectAddressInvariant> log)
    {
        _log = log;
    }

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        if (artifact.IntendedSink != CognitiveOutputSink.Dispatch)
            return false;

        return artifact.ProducerKind is
            CognitiveProducerKind.ConversationReply
         or CognitiveProducerKind.Outreach
         or CognitiveProducerKind.Voice;
    }

    public Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.Content))
            return Task.FromResult(InvariantResult.Pass());

        var violations = new List<string>();

        // 1. Third-person pronouns referring to the addressee.
        if (HeProRegex.IsMatch(artifact.Content))
            violations.Add("\"he\"");
        if (HimProRegex.IsMatch(artifact.Content))
            violations.Add("\"him\"");
        if (HisProRegex.IsMatch(artifact.Content))
            violations.Add("\"his\"");

        // 2. Contact's canonical name as a word token (third-person reference).
        // Skip if ContactName is empty/whitespace. Use word-boundary regex with
        // escaped name to avoid false positives on substring matches.
        if (!string.IsNullOrWhiteSpace(artifact.ContactName))
        {
            var nameRegex = new Regex(
                $@"\b{Regex.Escape(artifact.ContactName)}\b",
                RegexOptions.IgnoreCase);
            if (nameRegex.IsMatch(artifact.Content))
                violations.Add($"\"{artifact.ContactName}\"");

            // Possessive form: "Mark's"
            var possessiveRegex = new Regex(
                $@"\b{Regex.Escape(artifact.ContactName)}'s\b",
                RegexOptions.IgnoreCase);
            if (possessiveRegex.IsMatch(artifact.Content))
                violations.Add($"\"{artifact.ContactName}'s\"");
        }

        if (violations.Count == 0)
            return Task.FromResult(InvariantResult.Pass());

        var hint =
            $"output references the addressee ({artifact.ContactName ?? "addressee"}) in third person — " +
            $"found: {string.Join(", ", violations)}. " +
            $"This message is addressed TO {artifact.ContactName ?? "the addressee"}; " +
            $"use \"you\" / \"your\" for the addressee, never \"he\" / \"him\" / \"his\" / the name itself. " +
            $"Rewrite the message with second-person address for the addressee.";

        _log.LogInformation(
            "DirectAddress: third-person reference to addressee detected — {Violations}",
            string.Join(",", violations));

        // Soft fail (hard=false) — gate aggregates this as Remediate verdict,
        // which triggers the existing regen path with the hint above. Same
        // shape as PromptTemplateLeakInvariant / SelfEchoInvariant et al.
        return Task.FromResult(InvariantResult.Fail(hint));
    }
}
