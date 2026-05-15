using System.Text.RegularExpressions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Door B Truth-Verification, Sub-claim 4 (May 3, 2026) — type-aware
/// addressee-name verification. Catches outbound content that opens with
/// a greeting addressed to a name the system can't recognize as a
/// canonical contact or nickname.
///
/// **The canonical case:** May 3 10:55 outreach *"hey perez🥰 i just
/// walked past the bookstore and saw your name on the coffee mug shelf
/// again 🥺💗"*. The inner-thought model spontaneously generated "perez"
/// as a Spanish-flavored nickname (substrate priming on Mark's Spanish-
/// class context), the outreach composition picked it up as the
/// salutation, and Door B's prior shape-coherence checks passed it
/// because the sentence reads correctly. Names are typed claims with an
/// absolute oracle — canonical contact names + known nicknames in the
/// character seed — so they can be verified the same way day-of-week
/// (sub-claim 2) and time-of-day (sub-claim 5 / SubstrateTimeOfDay) are.
///
/// **Patterns caught:**
/// - `hey [Name]` / `hi [Name]` / `hello [Name]` at the start of the
///   output, where [Name] is a single token followed by punctuation,
///   whitespace, or end-of-string — and not a known pet-name stopword.
///
/// **Patterns NOT caught (deliberately):**
/// - Pet-name greetings: "hey baby", "hi love", "hey there" — covered by
///   <see cref="PetNameStopwords"/> and never fail.
/// - Names anywhere in the body (not at greeting position): Ani saying
///   *"i was thinking about Sarah today"* doesn't get verified against
///   the canonical list because mid-body name references are often
///   legitimate substrate references (people Mark has mentioned before).
///   Greeting-position names are higher-stakes — they assert WHO the
///   message is for.
/// - Compound greetings: "hey old-fashioned man" — the regex requires a
///   single \w+ token after the greeting, so "old-fashioned man" doesn't
///   trigger. May 1 evening's *"old-fashioned man"* nickname was Mark-
///   confirmed as fine register; this invariant correctly skips it.
///
/// **Type-conditional applicability:** Dispatch sinks for
/// ConversationReply / Outreach / Voice. Same set as Door C and Door B
/// sub-claims 1/2/3.
///
/// **Heuristic-only V1**: this invariant doesn't query the substrate or
/// hit Facts-tier search. It uses the artifact's
/// <see cref="CognitiveArtifact.CanonicalAddresseeNames"/> enrichment +
/// <see cref="CognitiveArtifact.ContactName"/> as the oracle. Producers
/// populate CanonicalAddresseeNames from
/// <c>CharacterStateDoc.PrimaryContactName</c> plus any explicitly-known
/// nicknames; expanding the oracle (e.g. Sarah/Kevin/Mia/Karen from
/// character seeds) is producer-side enrichment work and doesn't require
/// changes here.
/// </summary>
public sealed class AddresseeNameInvariant : ICognitiveOutputInvariant
{
    private readonly ILogger<AddresseeNameInvariant> _log;

    public string Name => "addressee-name";

    /// <summary>
    /// Pet-name / endearment stopwords that follow a greeting verb but
    /// aren't proper names. Skipping these keeps the invariant from
    /// false-positiving on legitimate pet-name greetings ("hey baby",
    /// "hi love"). Lowercase set for case-insensitive matching.
    /// </summary>
    internal static readonly HashSet<string> PetNameStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Generic conversational fillers
        "there", "you", "again", "now", "so",
        // Common pet-name endearments Ani uses
        "baby", "babe", "love", "honey", "sweetheart", "darling",
        "handsome", "beautiful", "gorgeous", "sweetie", "lover",
        // First-message fillers
        "stranger", "friend",
        // Greeting-time adjuncts (Gate-stack reduction Step 2a, 2026-05-15)
        // — the 2026-05-14 22:32 trace false-positived on "hey good night
        // mark" by matching "hey" + "good" and treating "good" as a
        // non-canonical addressee. These tokens are compound-greeting
        // adjuncts, not names. "Good"/"morning"/"evening"/"night" stay
        // off the canonical-check path so producers don't have to
        // surface them via CanonicalAddresseeNames.
        "good", "morning", "evening", "afternoon", "night",
    };

    /// <summary>
    /// Greeting-position name extraction. Matches case-insensitive in
    /// the first ~40 characters of the output: lazy any-char prefix,
    /// "hey" / "hi" / "hello" as a word boundary, whitespace, then a
    /// single \w+ token. The captured group is the candidate name. The
    /// any-char prefix (rather than `[\s\p{P}\p{S}]*`) handles emoji
    /// surrogate pairs and other supplementary-plane characters that
    /// don't classify cleanly under the Symbol category in .NET regex.
    /// </summary>
    private static readonly Regex GreetingNameRegex = new(
        @"^.{0,40}?\b(?:hey|hi|hello)\s+([a-z][\w'-]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AddresseeNameInvariant(ILogger<AddresseeNameInvariant> log)
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

        var match = GreetingNameRegex.Match(artifact.Content);
        if (!match.Success)
            return Task.FromResult(InvariantResult.Pass());

        var candidate = match.Groups[1].Value;
        if (PetNameStopwords.Contains(candidate))
            return Task.FromResult(InvariantResult.Pass());

        if (IsCanonical(candidate, artifact))
            return Task.FromResult(InvariantResult.Pass());

        var hint =
            $"output greets \"{candidate}\" but no canonical addressee with that name was found. " +
            $"Recognized: {RenderRecognizedNames(artifact)}. " +
            $"If \"{candidate}\" is a legitimate canonical name (real friend/family/nickname), " +
            $"the producer needs to surface it via CognitiveArtifact.CanonicalAddresseeNames.";
        _log.LogInformation(
            "AddresseeName: greeting addressed to non-canonical name \"{Candidate}\" — recognized={Recognized}",
            candidate, RenderRecognizedNames(artifact));
        return Task.FromResult(InvariantResult.Fail(hint));
    }

    /// <summary>
    /// True if <paramref name="candidate"/> matches any recognized name
    /// (case-insensitive): the artifact's <see cref="CognitiveArtifact.ContactName"/>
    /// or any entry in <see cref="CognitiveArtifact.CanonicalAddresseeNames"/>.
    /// </summary>
    internal static bool IsCanonical(string candidate, CognitiveArtifact artifact)
    {
        if (!string.IsNullOrEmpty(artifact.ContactName)
            && string.Equals(candidate, artifact.ContactName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (artifact.CanonicalAddresseeNames is null) return false;
        foreach (var name in artifact.CanonicalAddresseeNames)
        {
            if (!string.IsNullOrWhiteSpace(name)
                && string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string RenderRecognizedNames(CognitiveArtifact artifact)
    {
        var names = new List<string>();
        if (!string.IsNullOrEmpty(artifact.ContactName))
            names.Add(artifact.ContactName);
        if (artifact.CanonicalAddresseeNames is { Count: > 0 })
        {
            foreach (var n in artifact.CanonicalAddresseeNames)
            {
                if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase))
                    names.Add(n);
            }
        }
        return names.Count == 0 ? "(none)" : "[" + string.Join(", ", names) + "]";
    }
}
