using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

// G.1 (2026-06-11) — gist injection no longer creates a second user-role
// message after Mark's actual current turn. When LeanConversationPromptDirectiveInSystem=true
// the gist folds into the system prompt; when false it prepends the user
// prompt (legacy behavior). See Issue #92 §G.1.

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
    /// M.1 (May 5, 2026 evening) — compose the gist, emit telemetry, and
    /// inject the gist as a substrate block at the top of the user prompt
    /// when content is present and the flag is on. Returns the (possibly-
    /// modified) user prompt; callers pass the result to the LLM.
    ///
    /// **Behavior matrix:**
    /// - Composer null OR throws OR returns Empty → returns the original
    ///   prompt unchanged. No injection.
    /// - Composer returns content AND <c>ConsciousSubstrateGistEnabled</c>
    ///   is true → prepends the gist as a single-paragraph substrate block
    ///   followed by a blank line and the original prompt.
    /// - <c>ConsciousSubstrateGistEnabled</c> is false → composer typically
    ///   returns Empty (M.1 composer respects the flag), so no injection.
    ///   The flag is checked here defensively even if a future composer
    ///   produces content unconditionally.
    ///
    /// Telemetry (<c>M0_GIST_COMPOSITION</c>, <c>M0_GIST_SUBSTRATE_RATIO</c>)
    /// is emitted before injection so the substrate-share-by-source baseline
    /// accumulates regardless of injection success.
    ///
    /// **Best-effort:** never throws. On any failure, returns the original
    /// prompt unchanged and logs at Warning level.
    /// </summary>
    public static async Task<PromptPair> ComposeAndInjectAsync(
        IConsciousSubstrateGist?  composer,
        ContextSnapshot           snapshot,
        AniOptions                aniOptions,
        string                    promptSystemText,
        string                    promptUserText,
        bool                      directiveInSystem,
        ILogger                   log,
        CancellationToken         ct)
    {
        if (composer is null) return new PromptPair(promptSystemText, promptUserText);

        try
        {
            var gist = await composer.ComputeGistAsync(snapshot, ct).ConfigureAwait(false);

            EmitTelemetry(gist, promptUserText, aniOptions, log);

            // Injection path: only when flag is on AND gist has content.
            // Defensive flag-check so a future composer that ignores the flag
            // can't accidentally inject gist content into a flag-off deployment.
            if (!aniOptions.ConsciousSubstrateGistEnabled || gist.IsEmpty)
                return new PromptPair(promptSystemText, promptUserText);

            // G.1 (2026-06-11) — the gist routes to SYSTEM when the directive
            // is system-side, USER when it's user-side.
            //
            // Empirical anchor: 2026-06-10 22:17 production self-echo cascade.
            // Previously this method unconditionally prepended the gist to the
            // user prompt. When LeanConversationPromptDirectiveInSystem=true,
            // `promptUserText` is empty (the directive moved to system), so the
            // gist alone became a non-empty user-role turn — landing AFTER
            // Mark's actual current turn from RecentHistory. The model saw the
            // gist content (closedConversation + worldSelf — derived from her
            // own past outputs) as a fresh user message and lifted phrasings.
            //
            // Folding the gist into the system message when the directive is
            // system-side keeps it as framing (out-of-band substrate) rather
            // than turn-adjacent content. Mark's actual current turn from
            // RecentHistory remains the only user-role content the model sees.
            //
            // §4.6 composition rule: "merged into prose, not enumerated."
            // Foundation Input Phase 1 baseline telemetry (2026-08-13). Emits
            // structured F1_GIST_INJECTION per injection so Phase 1 can measure:
            // surface (system vs user role), gist size, slice count, and
            // directive-in-system flag state at time of injection. Referenced
            // by ANI-Composer-Input-Provenance-Audit-2026-08-13.md as L2.
            log.LogInformation(
                "F1_GIST_INJECTION surface={Surface} directiveInSystem={DirectiveInSystem} chars={Chars} sliceFlags={SliceFlags}",
                directiveInSystem ? "system" : "user",
                directiveInSystem,
                gist.Composed.Length,
                gist.Slices.ToString());

            if (directiveInSystem)
            {
                var mergedSystem = string.IsNullOrEmpty(promptSystemText)
                    ? gist.Composed
                    : promptSystemText + "\n\n" + gist.Composed;
                return new PromptPair(mergedSystem, promptUserText);
            }

            // Legacy path (directive in user) — gist prepends user prompt.
            return new PromptPair(promptSystemText, gist.Composed + "\n\n" + promptUserText);
        }
        catch (Exception ex)
        {
            // Best-effort — failures must not propagate; gist-side issues
            // never block dispatch.
            log.LogWarning(
                ex,
                "M1_GIST_FAILURE — composition / injection failed; using original prompt unchanged.");
            return new PromptPair(promptSystemText, promptUserText);
        }
    }

    /// <summary>
    /// Backward-compat observational entry point used by paths that have
    /// not yet migrated to <see cref="ComposeAndInjectAsync"/>. Emits the
    /// same telemetry but does not inject. Outreach composition path
    /// (M.4 deliverable) will use <see cref="ComposeAndInjectAsync"/>;
    /// other consumers can use this for telemetry-only.
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
            EmitTelemetry(gist, promptUserText, aniOptions, log);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "M0_GIST_FAILURE — observational pass failed; dispatch continues uninterrupted");
        }
    }

    private static void EmitTelemetry(
        ConsciousSubstrateGist  gist,
        string                  promptUserText,
        AniOptions              aniOptions,
        ILogger                 log)
    {
        var promptTokens = ApproxTokens(promptUserText);
        var ratio = promptTokens > 0
            ? (double)gist.TokenCount / promptTokens
            : 0.0;

        log.LogInformation(
            "M0_GIST_COMPOSITION enabled={Enabled} slices=closed:{Closed},innerThought:{InnerThought},registerState:{RegisterState},contactState:{ContactState},worldSelf:{WorldSelf},tensionState:{TensionState} sliceTokens=closed:{ClosedTokens},innerThought:{InnerThoughtTokens},registerState:{RegisterStateTokens},contactState:{ContactStateTokens},worldSelf:{WorldSelfTokens},tensionState:{TensionStateTokens} totalTokens={TotalTokens}",
            aniOptions.ConsciousSubstrateGistEnabled,
            gist.Slices.ClosedConversation,
            gist.Slices.InnerThoughtAggregate,
            gist.Slices.RegisterState,
            gist.Slices.ContactState,
            gist.Slices.WorldSelf,
            gist.Slices.TensionState,
            gist.SliceTokens.ClosedConversation,
            gist.SliceTokens.InnerThoughtAggregate,
            gist.SliceTokens.RegisterState,
            gist.SliceTokens.ContactState,
            gist.SliceTokens.WorldSelf,
            gist.SliceTokens.TensionState,
            gist.TokenCount);

        log.LogInformation(
            "M0_GIST_SUBSTRATE_RATIO gistTokens={GistTokens} promptTokens={PromptTokens} ratio={Ratio:F3}",
            gist.TokenCount,
            promptTokens,
            ratio);
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
