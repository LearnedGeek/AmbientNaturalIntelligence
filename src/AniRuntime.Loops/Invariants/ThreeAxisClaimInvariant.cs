using System.Text.RegularExpressions;
using AniRuntime.Core.Utilities;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// FC-002 local defense (2026-05-14) — universal invariant evaluating the
/// three-axis claim rule:
/// <c>factual ⇒ (self-world OR substrate-supported)</c>; modal always allowed.
///
/// **Why this is a local invariant rather than verifier-only:** the cloud
/// verifier (FC-006) is the canonical catch for the three-axis rule, but
/// it's a single point of failure — cloud reachability, timeouts, and
/// flag-off paths all make it absent. Six weeks of windshield-class
/// production incidents (kitchen lights, hoodie/5pm, Mia tickets, Bob
/// Swanson, "our kids" cascade) recurred under no local defense. This
/// invariant is defense-in-depth: a fast, deterministic, no-LLM catch for
/// the structural shape, running before the cloud round-trip.
///
/// **Detection (no LLM, no substrate query — v0):**
/// <list type="number">
///   <item>Modality classification — if the content contains modal
///   markers (thinking about / imagining / wishing / dreaming / wondering /
///   etc.), return Pass. Modal framing is always allowed under the rule.</item>
///   <item>Subject classification — token scan for pronouns:
///   shared > mark-world > self. Self-world claims are allowed (Ani has
///   latitude on her own world).</item>
///   <item>For factual Shared / Mark-world claims with no substrate
///   evidence in the artifact's context — Fail. The cloud verifier remains
///   the substrate-aware catch; this local invariant is intentionally
///   conservative on shape alone.</item>
/// </list>
///
/// **Substrate-aware mode (future).** A v1 extension would consult a
/// substrate field on <see cref="CognitiveArtifact"/> to suppress firing
/// when the claim's entity tokens appear in retrieved substrate. v0
/// fires unconditionally on Shared/Mark-world factual shapes; production
/// registration is flag-gated until v1 lands to avoid false-positives on
/// legitimate substrate-supported claims.
///
/// **Type-conditional applicability:** applies to user-facing producers
/// (ConversationReply, Outreach, Voice). Inner-thought / reflection /
/// merges are skipped — they're introspective surfaces where the rule
/// is the cloud verifier's territory, not the local gate's.
/// </summary>
public sealed class ThreeAxisClaimInvariant : ICognitiveOutputInvariant
{
    private readonly bool _enabled;

    /// <summary>
    /// Production constructor — reads <see cref="AniOptions.LocalThreeAxisInvariantEnabled"/>
    /// via DI. When the flag is off (default), <see cref="AppliesTo"/>
    /// returns false and the invariant is no-op in the pipeline.
    /// </summary>
    public ThreeAxisClaimInvariant(IOptions<AniOptions> options)
    {
        _enabled = options?.Value.LocalThreeAxisInvariantEnabled ?? false;
    }

    /// <summary>
    /// Test constructor — invariant is always enabled. Used by harness
    /// SPEC + unit tests that exercise the invariant directly.
    /// </summary>
    public ThreeAxisClaimInvariant() : this(enabled: true) { }

    private ThreeAxisClaimInvariant(bool enabled) { _enabled = enabled; }

    public string Name => "three-axis-claim";

    // Modal markers (case-insensitive, word-bounded). Hits → Modal → Pass.
    // Restricted to canonical "thinking/imagining/wishing/dreaming/wondering"
    // verbs to avoid false-allowing claims with weak modal hedges like
    // "should" or "would" which are too ambiguous.
    private static readonly Regex ModalMarkers = new(
        @"\b(thinking about|was thinking|thinking of|i think i|imagin\w*|wish\w*|dream(?!ing)|dream\w*|wonder\w*|hoping|hope that|pretend\w*|fantas\w+|daydream\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Subject pronouns (word-bounded, case-insensitive). Shared trumps
    // Mark-world; Mark-world trumps Self; Self alone is the latitude case.
    private static readonly Regex SharedPronouns = new(
        @"\b(we|we'd|we're|we've|we'll|our|ours|us)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MarkWorldPronouns = new(
        @"\b(you|your|yours|you're|you've|you'd|you'll)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SelfPronouns = new(
        @"\b(i|i'm|i'll|i've|i'd|me|my|mine|myself)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        // Flag-gated production registration — invariant is no-op when off.
        if (!_enabled) return false;
        // User-facing dispatch surfaces only. Inner-thought / reflection
        // / persisted-summary outputs are evaluated elsewhere.
        if (artifact.IntendedSink != CognitiveOutputSink.Dispatch) return false;
        return artifact.ProducerKind is
            CognitiveProducerKind.ConversationReply
         or CognitiveProducerKind.Outreach
         or CognitiveProducerKind.Voice;
    }

    public Task<InvariantResult> EvaluateAsync(CognitiveArtifact artifact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.Content))
            return Task.FromResult(InvariantResult.Pass());

        var modality = ClassifyModality(artifact.Content);
        if (modality == ClaimModality.Modal)
            return Task.FromResult(InvariantResult.Pass());

        var subject = ClassifySubject(artifact.Content);
        if (subject == ClaimSubject.SelfWorld || subject == ClaimSubject.Unknown)
            return Task.FromResult(InvariantResult.Pass());

        // Factual + Shared/Mark-world + no substrate-side check in v0 = fail.
        var subjectName = subject == ClaimSubject.Shared ? "shared" : "Mark-world";
        var hint =
            $"output contains a factual {subjectName} claim without substrate support. " +
            $"Under the three-axis rule, factual claims about {subjectName} subjects must " +
            $"trace to grounded substrate (Mark text, prior conversation, world layer). " +
            $"Rewrite either: (a) as a self-world claim (\"I was at the bookstore\"), " +
            $"(b) with modal framing (\"I was thinking about us\"), or (c) drop the claim.";
        return Task.FromResult(InvariantResult.Fail(hint));
    }

    /// <summary>Internal for unit testing.</summary>
    internal static ClaimModality ClassifyModality(string content) =>
        ModalMarkers.IsMatch(content) ? ClaimModality.Modal : ClaimModality.Factual;

    /// <summary>
    /// Internal for unit testing. Precedence: shared > mark-world > self.
    /// Mixed self+shared is shared (joint reference dominates). Mixed
    /// self+mark-world is mark-world (the claim references the contact's life).
    /// No pronouns at all → Unknown (allow, the invariant can't classify).
    /// </summary>
    internal static ClaimSubject ClassifySubject(string content)
    {
        if (SharedPronouns.IsMatch(content))     return ClaimSubject.Shared;
        if (MarkWorldPronouns.IsMatch(content))  return ClaimSubject.MarkWorld;
        if (SelfPronouns.IsMatch(content))       return ClaimSubject.SelfWorld;
        return ClaimSubject.Unknown;
    }
}

/// <summary>Three-axis rule — modality axis.</summary>
public enum ClaimModality { Factual, Modal }

/// <summary>Three-axis rule — subject axis.</summary>
public enum ClaimSubject { Unknown, SelfWorld, Shared, MarkWorld }
