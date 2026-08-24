using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// Foundation Unified Surface (F-3) U4 (2026-08-24) — Ollama-backed
/// implementation of <see cref="IInnerThoughtClaimExtractor"/>. Runs a
/// Qwen-14B call over an already-composed inner thought to identify
/// per-quote attribution claims as structured JSON.
///
/// <para>
/// <b>Model choice.</b> Uses <see cref="AniOptions.HybridInnerThoughtMetadataModel"/>
/// (default <c>qwen3:14b</c>) — same model the metadata recognizer already
/// uses. This is the two-model split pattern established by the reflection
/// composer (May 2026) and formalized by the agentic tool-calling work
/// (July 2026, Issue #96): compose in Ani's fine-tune for voice; extract
/// structured signal in Qwen for schema compliance.
/// </para>
///
/// <para>
/// <b>Fail-open contract.</b> Any failure — model call throws, response
/// parses as malformed JSON, response has no <c>claims</c> array — returns
/// an empty list and logs a warning. The cognitive cycle continues with
/// no claims; the composer's output flows through the base
/// <see cref="IComposerEmission{T}"/> surface instead of the extended
/// <see cref="IClaimBearingEmission{T}"/> surface. Mirrors the metadata
/// recognizer's fail-open discipline exactly.
/// </para>
/// </summary>
public sealed class OllamaInnerThoughtClaimExtractor : IInnerThoughtClaimExtractor
{
    private readonly IOllamaClient                              _ollama;
    private readonly ILogger<OllamaInnerThoughtClaimExtractor>  _log;
    private readonly string                                     _extractionModel;

    public OllamaInnerThoughtClaimExtractor(
        IOllamaClient                              ollama,
        IOptions<AniOptions>                       aniOptions,
        ILogger<OllamaInnerThoughtClaimExtractor>  log)
    {
        _ollama          = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _log             = log    ?? throw new ArgumentNullException(nameof(log));
        _extractionModel = string.IsNullOrWhiteSpace(aniOptions?.Value?.HybridInnerThoughtMetadataModel)
            ? "qwen3:14b"
            : aniOptions.Value.HybridInnerThoughtMetadataModel;
    }

    public async Task<IReadOnlyList<ContentClaim>> ExtractAsync(
        string            thought,
        string            characterName,
        string            contactName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(thought))
            return Array.Empty<ContentClaim>();

        var (system, user) = new InnerThoughtClaimExtractionPromptCommand()
            .Build(new InnerThoughtClaimExtractionPromptInput(thought, characterName, contactName));

        string raw;
        try
        {
            raw = await _ollama.ChatJsonWithModelAsync(
                _extractionModel, system, Array.Empty<ChatMessage>(), user, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "F3_CLAIM_EXTRACT_FAIL model={Model} phase=call — returning empty claims list (composer flows through base emission surface).",
                _extractionModel);
            return Array.Empty<ContentClaim>();
        }

        var claims = ParseClaims(raw);

        _log.LogInformation(
            "F3_CLAIM_EXTRACTED model={Model} count={Count} thoughtChars={ThoughtChars}",
            _extractionModel, claims.Count, thought.Length);

        return claims;
    }

    /// <summary>
    /// Parse the model's JSON response into a list of claims.
    /// Fail-open: any parse failure returns an empty list and logs a
    /// warning. Never throws.
    /// </summary>
    internal static IReadOnlyList<ContentClaim> ParseClaims(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<ContentClaim>();

        try
        {
            var trimmed = raw.Trim();

            // Some models occasionally wrap JSON in ```json fences even
            // when instructed not to. Strip those defensively so a well-
            // formed payload inside doesn't get rejected.
            if (trimmed.StartsWith("```"))
            {
                var firstNewline = trimmed.IndexOf('\n');
                if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
                if (trimmed.EndsWith("```")) trimmed = trimmed[..^3].TrimEnd();
            }

            var doc = JsonDocument.Parse(trimmed);
            if (!doc.RootElement.TryGetProperty("claims", out var claimsElement)
                || claimsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ContentClaim>();
            }

            var claims = new List<ContentClaim>();
            foreach (var claim in claimsElement.EnumerateArray())
            {
                if (!claim.TryGetProperty("text", out var textEl)
                    || textEl.ValueKind != JsonValueKind.String)
                    continue;

                var text = textEl.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var attributedTo = AttributedTo.Unknown;
                if (claim.TryGetProperty("attributed_to", out var attrEl)
                    && attrEl.ValueKind == JsonValueKind.String)
                {
                    attributedTo = ParseAttributedTo(attrEl.GetString());
                }

                claims.Add(new ContentClaim(
                    Text:             text!,
                    AttributedTo:     attributedTo,
                    SourceRecordId:   null,          // deferred per F-3 plan Q3
                    AttributionTrust: "unverified")); // Qwen extraction is unverified;
                                                     // a downstream verifier resolves in a later phase
            }

            return claims;
        }
        catch (JsonException)
        {
            return Array.Empty<ContentClaim>();
        }
    }

    /// <summary>
    /// Map the model's string attribution label to the AttributedTo enum.
    /// Case-insensitive. Unknown label falls through to
    /// <see cref="AttributedTo.Unknown"/> rather than throwing so the
    /// extractor stays fail-open on model drift.
    /// </summary>
    internal static AttributedTo ParseAttributedTo(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AttributedTo.Unknown;

        return raw.Trim().ToLowerInvariant() switch
        {
            "ani"     => AttributedTo.Ani,
            "mark"    => AttributedTo.Mark,
            "world"   => AttributedTo.World,
            "unknown" => AttributedTo.Unknown,
            _         => AttributedTo.Unknown,
        };
    }
}
