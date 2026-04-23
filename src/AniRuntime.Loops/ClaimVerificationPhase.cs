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
    /// </summary>
    private async Task<bool> IsClaimSupportedAsync(ExtractedClaim claim, CancellationToken ct)
    {
        var threshold = (float)_options.ClaimVerificationThreshold;
        var topK      = Math.Max(3, _options.ClaimVerificationMaxMemories);
        var queryText = BuildQueryText(claim);

        // Source 1: Facts tier semantic search.
        try
        {
            var factsResults = await _search.SearchByTierAsync(queryText, EpistemicTier.Facts, topK, ct)
                .ConfigureAwait(false);
            if (factsResults.Any(r => r.CosineSimilarity >= threshold))
            {
                _log.LogDebug("Claim supported by Facts tier: \"{Text}\"", claim.Text);
                return true;
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

                if (string.IsNullOrWhiteSpace(text)) continue;

                result.Add(new ExtractedClaim(text, type, keyTerms));
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
/// `Text` is the phrase from the message. `Type` is one of the five claim
/// categories (mark-action, mark-decision, shared-event, shared-decision,
/// shared-presence). `KeyTerms` are the specific entities or actions that
/// would need to appear in a corroborating source.
/// </summary>
public record ExtractedClaim(string Text, string Type, IReadOnlyList<string> KeyTerms);

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
