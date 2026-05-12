using System.Diagnostics;
using System.Text;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DispatchVerdict = AniRuntime.Core.Models.OutputGateVerdict;

namespace AniRuntime.Loops.Pipeline;

/// <summary>
/// Theme P Phase P.1 (May 11, 2026) — cross-class verification Post-stage
/// handler. Routes every dispatch-bound ConversationReply / Outreach /
/// Voice artifact through an <see cref="IFrontierVerifierClient"/>
/// (Anthropic Sonnet in P.1) so a different model class than the local
/// Ani-v7 generator evaluates the composed message before dispatch.
///
/// **Why this exists (plan-doc §1):** a model that generates a claim with
/// high confidence will verify that claim with high confidence — they
/// share the same weights. Same-class self-verification cannot reliably
/// catch high-confidence fabrications. Cross-class independence (verifier
/// from a different training lineage than the generator) breaks the
/// confidence-pattern transfer.
///
/// **Architectural shape (plan-doc §9.1, May 11 21:36 CDT correction):
/// additive defense in depth, NOT replacement.** This handler runs in
/// parallel to the existing local judgment invariants — both stacks fire
/// on every dispatch. The handler's <see cref="AppliesTo"/> consults
/// ONLY <see cref="AniOptions.FrontierVerifierEnabled"/>; it does NOT
/// reach into the applicability of any other handler. Local invariants
/// (InnerThoughtBleed, AddresseeName, TemporalAnchor, StateNow,
/// SubstrateTimeOfDay, ClaimVerificationPhase, plus the format gates)
/// have ZERO knowledge of this flag and continue to fire as they did
/// before Theme P.
///
/// **Failure mode = graceful degradation (plan-doc §4 lock 4):** if the
/// cloud verifier is unreachable, errors, or returns unparseable output,
/// the handler logs <c>P_VERIFIER_FALLBACK</c> and returns
/// <c>Continue</c>. The local judgment gates remain active on the same
/// dispatch — cloud absence reduces defense-in-depth by one layer, NOT
/// to zero.
///
/// **Substrate sources (plan-doc §9.1):** canonically-Mark-asserted only.
/// <see cref="ContextSnapshot.GroundedFacts"/> (Facts-tier records:
/// character seeds, perception events, user-asserted content) +
/// <see cref="ContextSnapshot.AnchoredMemories"/> (foundation memories
/// that never fade). NO fallback to <c>RecentExchanges</c>. NO
/// role-filter on Episodic records. NO reading the artifact's
/// <c>ContactRecentMessages</c>. The right architectural fix for
/// substrate gaps is to populate the canonical sources correctly at
/// retrieval-time, never to filter at construction-time.
/// </summary>
public sealed class FrontierVerifierHandler : ICognitivePipelineHandler
{
    private readonly IFrontierVerifierClient            _client;
    private readonly AniOptions                         _options;
    private readonly ILogger<FrontierVerifierHandler>   _log;

    public FrontierVerifierHandler(
        IFrontierVerifierClient            client,
        IOptions<AniOptions>               options,
        ILogger<FrontierVerifierHandler>   log)
    {
        _client  = client  ?? throw new ArgumentNullException(nameof(client));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public PipelineStage Stage => PipelineStage.Post;

    public string Name => "frontier-verifier";

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        if (artifact is null) return false;

        // Cloud handler kill-switch (plan-doc §4 lock 7). When false, the
        // cloud verifier is skipped; local judgment gates remain active
        // independently (they have no knowledge of this flag).
        if (!_options.FrontierVerifierEnabled) return false;

        // Only on dispatch-bound artifacts. Persistence and context-only
        // sinks have their own (non-cloud) coverage paths.
        if (artifact.IntendedSink != CognitiveOutputSink.Dispatch) return false;

        return artifact.ProducerKind is
            CognitiveProducerKind.ConversationReply
         or CognitiveProducerKind.Outreach
         or CognitiveProducerKind.Voice;
    }

    public async Task<HandlerResult> HandleAsync(
        CognitivePipelineContext ctx, CancellationToken ct)
    {
        if (ctx is null)            throw new ArgumentNullException(nameof(ctx));
        if (ctx.Artifact is null)   throw new ArgumentNullException(nameof(ctx.Artifact));

        // Empty content: nothing to verify. Continue (pass-through) so
        // downstream handlers (or the dispatcher) can decide what an empty
        // composed message means in their own contracts. Verifier client
        // is NOT called.
        if (string.IsNullOrWhiteSpace(ctx.Artifact.Content))
            return HandlerResult.Continue("empty content — nothing to verify");

        var request = BuildRequest(ctx);

        var sw = Stopwatch.StartNew();
        FrontierVerifierResult result;
        try
        {
            result = await _client.VerifyAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation — propagate up. The pipeline orchestrator
            // catches OperationCanceledException tied to ct and emits its
            // own short-circuit.
            throw;
        }
        catch (Exception ex) when (
            ex is TimeoutException
               or HttpRequestException
               or InvalidOperationException
               or TaskCanceledException)
        {
            sw.Stop();
            // Graceful degradation (plan-doc §4 lock 4): log fallback +
            // Continue. The local judgment invariants are running on the
            // same dispatch in parallel — cloud absence reduces defense-
            // in-depth by one layer, not to zero. No state mutation, no
            // substrate routing — the local stack does not care that the
            // cloud handler ran or didn't.
            _log.LogInformation(
                "P_VERIFIER_FALLBACK reason=\"{Reason}\" — local judgment gates remain active",
                ex.Message);
            return HandlerResult.Continue($"verifier error (fallback): {ex.Message}");
        }

        sw.Stop();

        // Build the five-flag q1..q5 string for the telemetry line. 'V'
        // marks a violation; '0' marks no violation. Matches plan-doc §6.
        var flags = BuildFlagsString(result);

        if (result.AnyViolation)
        {
            var reason = result.AggregatedReason ?? "frontier verifier flagged violation(s)";
            _log.LogInformation(
                "P_VERIFIER_VERDICT verdict=Remediate {Flags} duration_ms={Duration} provider=Sonnet",
                flags, sw.ElapsedMilliseconds);
            return HandlerResult.ShortCircuitWith(DispatchVerdict.Remediate, reason);
        }

        _log.LogInformation(
            "P_VERIFIER_VERDICT verdict=Pass {Flags} duration_ms={Duration} provider=Sonnet",
            flags, sw.ElapsedMilliseconds);
        return HandlerResult.Continue("frontier verifier pass");
    }

    // ─── Substrate rendering (plan-doc §9.1: canonical sources only) ─────

    /// <summary>
    /// Build the verifier request from the snapshot's canonical-Mark-only
    /// pools. Substrate sources match plan-doc §9.1 — Facts-tier
    /// <see cref="ContextSnapshot.GroundedFacts"/> for Mark-asserted, and
    /// <see cref="ContextSnapshot.AnchoredMemories"/> for canonical. No
    /// other sources are read. Empty pools render as empty strings.
    /// </summary>
    private static FrontierVerifierRequest BuildRequest(CognitivePipelineContext ctx)
    {
        var snapshot = ctx.Snapshot;
        var artifact = ctx.Artifact;

        var markAsserted = RenderRecords(
            snapshot?.GroundedFacts?
                .OrderByDescending(f => f.CreatedAt)
                .Take(10));

        var canonical = RenderRecords(
            snapshot?.AnchoredMemories?.Take(10));

        var character = snapshot?.CharacterState;

        var addressee = !string.IsNullOrWhiteSpace(character?.PrimaryContactName)
            ? character.PrimaryContactName
            : string.Empty;

        var knownContacts = character?.CanonicalContacts is { Count: > 0 } contacts
            ? string.Join(", ", contacts)
            : string.Empty;

        // Day-of-week derives from local-time projection of GeneratedAt
        // so the verifier prompt's "Day of week:" line matches the local
        // calendar Mark experiences, not the UTC calendar.
        var localTime = artifact.GeneratedAt.LocalDateTime;

        return new FrontierVerifierRequest(
            ComposedMessage:        artifact.Content,
            MarkAssertedSubstrate:  markAsserted,
            CanonicalSubstrate:     canonical,
            CurrentTime:            artifact.GeneratedAt,
            CurrentDayOfWeek:       localTime.DayOfWeek.ToString(),
            AddresseeCanonicalName: addressee,
            KnownContacts:          knownContacts);
    }

    private static string RenderRecords(IEnumerable<MemoryRecord>? records)
    {
        if (records is null) return string.Empty;
        var lines = new List<string>();
        foreach (var r in records)
        {
            if (r is null) continue;
            var content = r.Content?.Trim();
            if (string.IsNullOrEmpty(content)) continue;
            lines.Add($"- {content}");
        }
        return string.Join("\n", lines);
    }

    private static string BuildFlagsString(FrontierVerifierResult result)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 5; i++)
        {
            var q = result.Questions.FirstOrDefault(x => x.QuestionNumber == i);
            sb.Append("q").Append(i).Append('=').Append(q is { Violation: true } ? 'V' : '0');
            if (i < 5) sb.Append(' ');
        }
        return sb.ToString();
    }
}
