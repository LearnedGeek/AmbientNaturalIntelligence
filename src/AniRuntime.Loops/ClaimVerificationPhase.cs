using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Feature 14 v2 — Outbound claim verification (restored April 22, 2026).
///
/// Runs after composition and before dispatch on both the outreach path
/// (OutreachPhase) and the conversation-reply path (ConversationReplyPhase).
/// Extracts claims about the contact from the composed message, corroborates
/// each against the existing tier architecture (Facts + anchored + inbound
/// "Mark said:" Episodic records), and returns a pass/suppress decision.
///
/// Architecture-over-instruction discipline, the Apr 22 redesign of the Feature 14
/// pattern:
///
///   • The extractor is asked ONLY to identify claims. No judgment, no rewrite,
///     no regeneration prompt. The model never hears "you were wrong, try again."
///   • Verification is a tier-provenance check using existing memory
///     infrastructure — Facts tier for ground-truth, Anchored for foundation,
///     Episodic "Mark said:" records for inbound statements.
///   • On verification failure the message is SUPPRESSED. The caller drops the
///     dispatch, records a silence event, and moves on. The composition model
///     is never told the message failed. Next cycle's retrieval substrate will
///     differ (new inbound, different Interior content) and composition will
///     naturally produce different output — that's the update signal.
///
/// The difference from the removed April-10 Feature 14: the original version
/// regenerated with an explicit negative-constraint prompt ("your composition
/// contained unsupported claims, regenerate without them"). That is instruction,
/// and the project's track record shows instruction degrades quality. This
/// version gates the channel, not the model.
///
/// Scope: claims about the contact's actions, decisions, shared events, shared
/// decisions, and shared presence. Explicitly OUT OF SCOPE: Ani's own canonical
/// world (bookstore, Wisconsin, shelving books, the desk), her feelings, her
/// thoughts, her wishes, and descriptions of the message itself. The World
/// Layer substrate stays free to elaborate; only cross-claims about Mark are
/// gated.
/// </summary>
public class ClaimVerificationPhase
{
    private readonly IMemorySearch                    _search;
    private readonly IOllamaClient                    _ollama;
    private readonly AniOptions                       _options;
    private readonly ILogger<ClaimVerificationPhase>  _log;

    public ClaimVerificationPhase(
        IMemorySearch                   search,
        IOllamaClient                   ollama,
        IOptions<AniOptions>            options,
        ILogger<ClaimVerificationPhase> log)
    {
        _search  = search;
        _ollama  = ollama;
        _options = options.Value;
        _log     = log;
    }

    /// <summary>
    /// Run the claim verification gate on a composed outbound message.
    /// Returns a result object the caller uses to decide dispatch vs. suppress.
    /// The method never throws — on unexpected failure it defaults to PASS so
    /// a gate bug cannot silence Ani entirely.
    /// </summary>
    public async Task<ClaimVerificationResult> VerifyAsync(
        string composedMessage, string contactName, CancellationToken ct)
    {
        if (!_options.ClaimVerificationEnabled)
            return ClaimVerificationResult.Pass("verification disabled by config", Array.Empty<ExtractedClaim>());

        if (string.IsNullOrWhiteSpace(composedMessage))
            return ClaimVerificationResult.Pass("empty message", Array.Empty<ExtractedClaim>());

        List<ExtractedClaim> claims;
        try
        {
            claims = await ExtractClaimsAsync(composedMessage, contactName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Claim extraction failed — defaulting to PASS (gate bug must not silence Ani)");
            return ClaimVerificationResult.Pass("extraction failure (default pass)", Array.Empty<ExtractedClaim>());
        }

        if (claims.Count == 0)
        {
            _log.LogDebug("Claim verification: 0 claims extracted — pass");
            return ClaimVerificationResult.Pass("no mark-domain or shared-history claims extracted", claims);
        }

        var unverified = new List<ExtractedClaim>();
        foreach (var claim in claims)
        {
            var supported = await IsClaimSupportedAsync(claim, ct).ConfigureAwait(false);
            if (!supported)
                unverified.Add(claim);
        }

        if (unverified.Count == 0)
        {
            _log.LogInformation(
                "Claim verification: {Count} claim(s) all supported — pass. Claims: {Claims}",
                claims.Count,
                string.Join("; ", claims.Select(c => $"[{c.Type}] \"{c.Text}\"")));
            return ClaimVerificationResult.Pass($"{claims.Count} claim(s) all verified", claims);
        }

        _log.LogWarning(
            "Claim verification: {Unverified} of {Total} claim(s) unsupported — SUPPRESS. Unverified: {Claims}",
            unverified.Count,
            claims.Count,
            string.Join("; ", unverified.Select(c => $"[{c.Type}] \"{c.Text}\" (key terms: {string.Join(",", c.KeyTerms)})")));

        return ClaimVerificationResult.Suppress(
            $"{unverified.Count} of {claims.Count} claim(s) unverified",
            claims,
            unverified);
    }

    /// <summary>
    /// Call the LLM extractor. Returns a list of structured claims, or empty
    /// if the model produced no parseable output. Failures here bubble up to
    /// VerifyAsync which defaults to PASS.
    /// </summary>
    private async Task<List<ExtractedClaim>> ExtractClaimsAsync(
        string message, string contactName, CancellationToken ct)
    {
        var (system, user) = PromptBuilder.BuildClaimExtractionPrompt(message, contactName);
        var raw = await _ollama.ChatJsonAsync(
            system, Array.Empty<ChatMessage>(), user, ct).ConfigureAwait(false);
        return ParseClaims(raw);
    }

    /// <summary>
    /// Corroborate a single claim against the three canonical sources:
    /// Facts tier (primary ground truth), Anchored memory (foundation), and
    /// inbound "Mark said:" / "Mark texted:" Episodic records.
    ///
    /// Uses the existing semantic search infrastructure (SearchByTierAsync).
    /// A claim is supported if any source produces a result with cosine
    /// similarity at or above ClaimVerificationThreshold.
    ///
    /// R1 Phase 1 (May 9, 2026): when the claim type is
    /// <c>shared-event-with-attribution</c>, cosine similarity is treated as
    /// necessary-but-not-sufficient. After the cosine pass produces top-k
    /// candidates above threshold, an additional LLM call per candidate
    /// asks: *"does record A describe the same event as claim B?"* with a
    /// JSON-locked yes/no contract. The claim is supported iff at least one
    /// candidate's event-shape verification returns yes. Fast path: zero
    /// cosine matches → unsupported, no LLM calls made.
    ///
    /// Performance bound: top-3 candidates × shared-event-with-attribution
    /// claims means ≤3 small LLM calls per such claim (typical claim is
    /// shorter than the cycle's ~1s composition call). Existing pipeline
    /// already issues several Ollama calls per cycle — this is acceptable.
    /// Other claim types (existing five) continue to use cosine-only
    /// verification, so the fast majority of claim traffic is unchanged.
    /// </summary>
    private async Task<bool> IsClaimSupportedAsync(ExtractedClaim claim, CancellationToken ct)
    {
        var threshold = (float)_options.ClaimVerificationThreshold;
        var topK      = Math.Max(3, _options.ClaimVerificationMaxMemories);
        var queryText = BuildQueryText(claim);

        // R1 Phase 1 — typed verification path. Routed here BEFORE the
        // legacy three-source cosine-only path so the typed claim type
        // gets its event-shape oracle, not the cosine oracle. All other
        // claim types fall through to the legacy path below.
        if (string.Equals(
                claim.Type,
                ClaimTypes.SharedEventWithAttribution,
                StringComparison.OrdinalIgnoreCase))
        {
            return await IsSharedEventWithAttributionSupportedAsync(
                claim, queryText, threshold, topK, ct).ConfigureAwait(false);
        }

        // Source 1: Facts tier semantic search — Mark-asserted records only.
        //
        // Apr 30, 2026 — read-side source-attribution filter (Theme J.5c).
        // The pre-Apr-30 verifier admitted any Facts-tier record as evidence
        // supporting a Mark-action claim, including:
        //   - RSS perceptions (Bon Appétit article → "cheesy chickpea toast"
        //     became "supported" because the food name was in the substrate
        //     from the reactive-share path that morning).
        //   - Ani's own outputs that ended up in Facts via downstream
        //     promotion paths.
        //   - Generic world-facts that happened to share keywords with the
        //     claim (Apr 30 desk-and-three-books false-positive).
        // None of those are Mark-asserted. The fix: only `twilio-inbound`
        // perceptions and `character-seed` records count as Mark-asserted
        // evidence for claims about Mark's actions / decisions / shared
        // events. Other Facts-tier content is still searched but excluded
        // from the support decision.
        try
        {
            var factsResults = await _search.SearchByTierAsync(queryText, EpistemicTier.Facts, topK, ct)
                .ConfigureAwait(false);
            var supportingFact = factsResults.FirstOrDefault(r =>
                r.CosineSimilarity >= threshold && IsMarkAssertedSource(r.Record));
            if (supportingFact is not null)
            {
                _log.LogDebug(
                    "Claim supported by Facts tier (source={Source}): \"{Text}\"",
                    supportingFact.Record.SourceName, claim.Text);
                return true;
            }

            // Log when we filtered out a non-Mark-asserted match — visibility
            // into how often the read-side filter is doing work.
            var filteredOut = factsResults.FirstOrDefault(r =>
                r.CosineSimilarity >= threshold && !IsMarkAssertedSource(r.Record));
            if (filteredOut is not null)
            {
                _log.LogDebug(
                    "J.5c attribution filter: rejected non-Mark-asserted Facts match (source={Source}, cosine={Cosine:F2}): \"{Preview}\"",
                    filteredOut.Record.SourceName, filteredOut.CosineSimilarity,
                    Truncate(filteredOut.Record.Content, 80));
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Facts tier search failed for claim verification — continuing with other sources");
        }

        // Source 2: Anchored memories — keyword match on key_terms. Anchored
        // memories may not all have embeddings; keyword match is the fast fallback.
        try
        {
            var anchored = await _search.GetAnchoredMemoriesAsync(ct).ConfigureAwait(false);
            if (claim.KeyTerms.Count > 0)
            {
                foreach (var mem in anchored)
                {
                    var lower = mem.Content.ToLowerInvariant();
                    var matches = claim.KeyTerms.Count(t => lower.Contains(t.ToLowerInvariant()));
                    // Require majority of key terms present
                    if (matches >= Math.Max(1, (claim.KeyTerms.Count + 1) / 2))
                    {
                        _log.LogDebug("Claim supported by anchored memory: \"{Text}\"", claim.Text);
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Anchored memory search failed for claim verification — continuing with other sources");
        }

        // Source 3: Inbound Mark messages — Episodic records starting with the
        // canonical "Mark said:" or "Mark texted:" prefix. This is the direct-
        // quote ground truth for what the contact has actually stated.
        try
        {
            var episodicResults = await _search.SearchByTierAsync(queryText, EpistemicTier.Episodic, topK * 2, ct)
                .ConfigureAwait(false);
            foreach (var scored in episodicResults)
            {
                var content = scored.Record.Content ?? "";
                if (!content.StartsWith("Mark said:",   StringComparison.OrdinalIgnoreCase)
                 && !content.StartsWith("Mark texted:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (scored.CosineSimilarity >= threshold)
                {
                    _log.LogDebug("Claim supported by inbound Mark statement: \"{Text}\"", claim.Text);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Episodic (inbound Mark) search failed for claim verification");
        }

        return false;
    }

    /// <summary>
    /// R1 Phase 1 (May 9, 2026) — typed event-shape verification for
    /// <c>shared-event-with-attribution</c> claims. Cosine similarity is
    /// used to find candidate Mark-asserted records (top-k above
    /// threshold), then each candidate is presented to a small LLM call
    /// asking: does record A describe the same specific event as claim B?
    /// The claim is supported iff at least one candidate's event-shape
    /// verification returns yes.
    ///
    /// Fast path: zero cosine matches → unsupported, no LLM calls made.
    /// Fail-closed: parse failure on the LLM response is treated as "no"
    /// (better to suppress than dispatch under uncertainty).
    /// </summary>
    private async Task<bool> IsSharedEventWithAttributionSupportedAsync(
        ExtractedClaim claim, string queryText, float threshold, int topK, CancellationToken ct)
    {
        // Step 1: gather candidate Mark-asserted Facts-tier records via
        // existing cosine search infrastructure. Top-k cap enforced for the
        // performance bound documented on IsClaimSupportedAsync.
        var candidates = new List<ScoredMemory>();
        try
        {
            var factsResults = await _search.SearchByTierAsync(
                queryText, EpistemicTier.Facts, topK, ct).ConfigureAwait(false);
            foreach (var r in factsResults)
            {
                if (r.CosineSimilarity >= threshold && IsMarkAssertedSource(r.Record))
                    candidates.Add(r);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "R1: Facts tier cosine search failed for shared-event-with-attribution claim — treating as no candidates");
        }

        // Cap at 3 candidates for the documented performance bound. Order
        // is preserved (highest-similarity first as returned by SearchByTierAsync).
        if (candidates.Count > 3)
            candidates = candidates.Take(3).ToList();

        // Step 2: fast-path. Zero candidates means the cosine pre-filter
        // already established no Mark-asserted record could plausibly
        // describe this event. No LLM calls; claim is unsupported.
        if (candidates.Count == 0)
        {
            _log.LogInformation(
                "R1_EVENT_VERIFY claim=\"{Claim}\" candidates_evaluated=0 verified=false (fast path: no cosine matches)",
                claim.Text);
            return false;
        }

        // Step 3: per-candidate event-shape verification. First yes wins.
        var verified = false;
        var evaluated = 0;
        foreach (var cand in candidates)
        {
            evaluated++;
            try
            {
                var (system, user) = PromptBuilder.BuildEventVerificationPrompt(
                    claim.Text, cand.Record.Content ?? string.Empty, "Mark");
                var raw = await _ollama.ChatJsonAsync(
                    system, Array.Empty<ChatMessage>(), user, ct).ConfigureAwait(false);

                if (ParseSameEventResponse(raw))
                {
                    _log.LogDebug(
                        "R1: shared-event-with-attribution claim verified by candidate (source={Source}, cosine={Cosine:F2}): \"{Text}\"",
                        cand.Record.SourceName, cand.CosineSimilarity, claim.Text);
                    verified = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                // Fail-closed on per-candidate exception: treat as "no".
                // Logging at Debug since the surrounding loop continues to
                // evaluate other candidates and the final R1_EVENT_VERIFY
                // line records the aggregate result.
                _log.LogDebug(ex,
                    "R1: event-shape verification LLM call failed for candidate — treating as no");
            }
        }

        _log.LogInformation(
            "R1_EVENT_VERIFY claim=\"{Claim}\" candidates_evaluated={Evaluated} verified={Verified}",
            claim.Text, evaluated, verified);

        return verified;
    }

    /// <summary>
    /// R1 Phase 1: parse the strict JSON-locked event-verification response
    /// shape <c>{"same_event": true|false}</c>. Fail-closed: any malformed
    /// or unrecognised payload returns <c>false</c>. The verifier prefers a
    /// false-negative (suppression of a possibly-true claim) over a
    /// false-positive (dispatch of a confab) — that is the gate's role.
    /// </summary>
    internal static bool ParseSameEventResponse(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("same_event", out var v)) return false;
            return v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Compose a retrieval query from the claim text plus key terms. The key
    /// terms usually improve embedding similarity with short, specific sources
    /// like inbound messages or character-seed facts.
    /// </summary>
    private static string BuildQueryText(ExtractedClaim claim)
    {
        if (claim.KeyTerms.Count == 0) return claim.Text;
        return $"{claim.Text} {string.Join(" ", claim.KeyTerms)}";
    }

    /// <summary>
    /// Apr 30, 2026 — Theme J.5c read-side source-attribution filter.
    /// Returns true when the memory record represents a Mark-asserted fact —
    /// either an inbound message Mark sent (twilio-inbound perception) or a
    /// curated character seed about Mark's life. Other Facts-tier content
    /// (RSS perceptions, world facts, residual Ani-output promoted to Facts
    /// by downstream paths) is excluded from claim-support decisions about
    /// Mark's actions / decisions / shared events, since those claims need
    /// MARK as the source of truth, not the substrate at large.
    /// </summary>
    internal static bool IsMarkAssertedSource(MemoryRecord record)
    {
        if (record is null) return false;
        var source = record.SourceName ?? string.Empty;

        // The two canonical Mark-asserted source paths.
        if (string.Equals(source, "twilio-inbound", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(source, "character-seed", StringComparison.OrdinalIgnoreCase)) return true;

        // Defense-in-depth content-prefix check for inbound perception
        // records that may not carry the source name through every code
        // path (older substrate may have empty SourceName but the content
        // shape — "Mark texted: …" / "Mark said: …" — still identifies
        // the assertion as Mark's).
        var content = record.Content ?? string.Empty;
        if (content.StartsWith("Mark texted:", StringComparison.OrdinalIgnoreCase)) return true;
        if (content.StartsWith("Mark said:",   StringComparison.OrdinalIgnoreCase)) return true;
        if (content.StartsWith("About Mark:",  StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? "") : s[..max] + "...";

    /// <summary>
    /// Parse the LLM JSON response into a list of structured claims. Tolerant of
    /// schema drift: missing fields default safely; unexpected values are logged
    /// and skipped rather than thrown. On any parse failure the list returns
    /// empty (treated as "no claims extracted" → PASS).
    /// </summary>
    internal static List<ExtractedClaim> ParseClaims(string? rawJson)
    {
        var result = new List<ExtractedClaim>();
        if (string.IsNullOrWhiteSpace(rawJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("claims", out var claimsElement))
                return result;
            if (claimsElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in claimsElement.EnumerateArray())
            {
                var text = item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var type = item.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                var keyTerms = new List<string>();
                if (item.TryGetProperty("key_terms", out var kt) && kt.ValueKind == JsonValueKind.Array)
                {
                    foreach (var k in kt.EnumerateArray())
                    {
                        var s = k.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) keyTerms.Add(s);
                    }
                }

                // R1 Phase 1 (May 9, 2026): optional event_actor field for
                // shared-event-with-attribution claims. Null/absent for older
                // schemas and for claim types that don't carry an actor; the
                // verifier reads it only when present.
                string? eventActor = null;
                if (item.TryGetProperty("event_actor", out var ea)
                    && ea.ValueKind == JsonValueKind.String)
                {
                    var s = ea.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) eventActor = s;
                }

                if (string.IsNullOrWhiteSpace(text)) continue;

                result.Add(new ExtractedClaim(text, type, keyTerms, eventActor));
            }
        }
        catch (JsonException)
        {
            // Return what we parsed (possibly empty). Don't throw — gate bugs must not silence Ani.
        }

        return result;
    }
}

/// <summary>
/// A single claim extracted from a composed outbound message.
/// `Text` is the phrase from the message. `Type` is one of the six claim
/// categories (mark-action, mark-decision, shared-event,
/// shared-event-with-attribution, shared-decision, shared-presence).
/// `KeyTerms` are the specific entities or actions that would need to
/// appear in a corroborating source. `EventActor` is populated only for
/// `shared-event-with-attribution` claims (R1 Phase 1, May 9 2026) and
/// names the actor performing the asserted action (e.g. "Mia", "we").
/// Optional / nullable so existing call sites and parsers are unaffected.
/// </summary>
public record ExtractedClaim(
    string Text,
    string Type,
    IReadOnlyList<string> KeyTerms,
    string? EventActor = null);

/// <summary>
/// R1 Phase 1 (May 9, 2026): canonical claim type discriminators. Defined
/// once so the verifier and the prompt builder agree on string values.
/// </summary>
public static class ClaimTypes
{
    public const string MarkAction                  = "mark-action";
    public const string MarkDecision                = "mark-decision";
    public const string SharedEvent                 = "shared-event";
    public const string SharedEventWithAttribution  = "shared-event-with-attribution";
    public const string SharedDecision              = "shared-decision";
    public const string SharedPresence              = "shared-presence";
}

/// <summary>
/// Result of claim verification. `Passed=true` means the composed message can
/// be dispatched. `Passed=false` means the caller should suppress dispatch and
/// record a silence event. In both cases, `Claims` lists everything extracted
/// so the caller can log or surface it for research review. `Unverified` is
/// populated only on the failure path.
/// </summary>
public record ClaimVerificationResult(
    bool Passed,
    string Reason,
    IReadOnlyList<ExtractedClaim> Claims,
    IReadOnlyList<ExtractedClaim> Unverified)
{
    public static ClaimVerificationResult Pass(string reason, IReadOnlyList<ExtractedClaim> claims)
        => new(true, reason, claims, Array.Empty<ExtractedClaim>());

    public static ClaimVerificationResult Suppress(
        string reason, IReadOnlyList<ExtractedClaim> claims, IReadOnlyList<ExtractedClaim> unverified)
        => new(false, reason, claims, unverified);
}
