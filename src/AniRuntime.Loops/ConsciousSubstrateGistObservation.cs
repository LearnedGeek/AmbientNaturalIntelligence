using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Theme M Phase M.0 (May 5, 2026) — observational telemetry helper for the
/// conscious-substrate gist composer. Wraps
/// <see cref="IConsciousSubstrateGist.ComputeGistAsync"/> and emits
/// <c>M0_GIST_COMPOSITION</c> + <c>M0_GIST_SUBSTRATE_RATIO</c> log lines.
///
/// **M.0 contract:** the no-op composer returns
/// <see cref="ConsciousSubstrateGist.Empty"/>; this helper still emits the
/// telemetry so the substrate-source-ratio baseline accumulates against the
/// pre-Theme-M state. The log lines record what M.1+ WOULD surface from the
/// gist substrate; the calling phase's prompt is unchanged.
///
/// **Best-effort semantics:** never throws. Observational instrumentation
/// MUST NOT affect dispatch — same property as the V1.5a observation
/// helper. Failures log at Warning level and return; the calling phase's
/// composition + dispatch continue uninterrupted.
///
/// **Substrate-source ratio (M0_GIST_SUBSTRATE_RATIO):** computed at the
/// consumer surface (this helper) rather than inside the composer, because
/// it depends on retrieval-block + character-seed token counts that the
/// composer does not see. The numerator is the gist token count; the
/// denominator is gist + retrieval + character-seed tokens, approximated
/// from the prompt text length / ~4 chars/token heuristic. Approximation is
/// acceptable for M.0 trend telemetry; M.2 telemetry build-out tightens.
///
/// See `docs/spec/ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md` §5
/// Phase M.0 for the full architectural framing.
/// </summary>
internal static class ConsciousSubstrateGistObservation
{
    /// <summary>
    /// Run the observational pass and emit telemetry. Always returns
    /// quickly; never throws.
    /// </summary>
    public static async Task ObserveAsync(
        IConsciousSubstrateGist?  composer,
        ContextSnapshot           snapshot,
        AniOptions                aniOptions,
        string                    promptUserText,
        ILogger                   log,
        CancellationToken         ct)
    {
        if (composer is null) return;

        try
        {
            var gist = await composer.ComputeGistAsync(snapshot, ct).ConfigureAwait(false);

            var promptTokens = ApproxTokens(promptUserText);
            var ratio = promptTokens > 0
                ? (double)gist.TokenCount / promptTokens
                : 0.0;

            log.LogInformation(
                "M0_GIST_COMPOSITION enabled={Enabled} slices=closed:{Closed},innerThought:{InnerThought},registerState:{RegisterState},contactState:{ContactState},worldSelf:{WorldSelf},tensionState:{TensionState} totalTokens={TotalTokens}",
                aniOptions.ConsciousSubstrateGistEnabled,
                gist.Slices.ClosedConversation,
                gist.Slices.InnerThoughtAggregate,
                gist.Slices.RegisterState,
                gist.Slices.ContactState,
                gist.Slices.WorldSelf,
                gist.Slices.TensionState,
                gist.TokenCount);

            log.LogInformation(
                "M0_GIST_SUBSTRATE_RATIO gistTokens={GistTokens} promptTokens={PromptTokens} ratio={Ratio:F3}",
                gist.TokenCount,
                promptTokens,
                ratio);
        }
        catch (Exception ex)
        {
            // Observational-only — failures must not propagate.
            log.LogWarning(
                ex,
                "M0_GIST_FAILURE — observational pass failed; dispatch continues uninterrupted");
        }
    }

    /// <summary>
    /// Cheap heuristic token count: ~4 characters per token for English text.
    /// M.2 telemetry build-out replaces this with a proper tokenizer call when
    /// the substrate-source-ratio metric becomes load-bearing for downstream
    /// decisions; for M.0 trend telemetry, the heuristic is sufficient.
    /// </summary>
    private static int ApproxTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}
