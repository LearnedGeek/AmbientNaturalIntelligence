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
/// **Substrate sources (plan-doc §9.1 + P.3.2, May 12 2026):**
/// canonically-Mark-asserted only, retrieved **post-hoc** by semantic
/// similarity against the composed reply text (<c>ctx.Artifact.Content</c>).
/// The retrieval surface is the same <see cref="IMemorySearch.SearchByTierAsync"/>
/// the composition pipeline uses, so the same three-way scoring
/// (cosine + importance + recency — Feature 20) decides which records
/// reach the verifier. Anchored character seeds surface naturally
/// because their boosted importance + cosine against claim-bearing
/// content ranks them high.
///
/// P.3.2 explicitly REPLACES the prior snapshot-pool reads
/// (<c>Snapshot.GroundedFacts.OrderByDescending(CreatedAt).Take(10)</c>
/// and <c>Snapshot.AnchoredMemories.Take(10)</c>). Those pools were
/// chronological-recency slices keyed to the cycle's perceptions, not
/// to the message about to ship — they routinely missed Anchored
/// foundation records (bookstore/Wisconsin/hoodie) the verifier needed
/// to confirm claims. The retrieval source is now what the model is
/// about to assert, scored against the entire Facts pool.
///
/// <c>CharacterState.CanonicalContacts</c> remains the source for
/// <c>KnownContacts</c> (unchanged). NO fallback to
/// <c>RecentExchanges</c>. NO role-filter on Episodic records. NO
/// reading the artifact's <c>ContactRecentMessages</c>.
///
/// Substrate-split note: <c>SearchByTierAsync</c> filters by
/// <see cref="EpistemicTier"/> (the <c>provenance</c> column), while
/// the foundation-memory flag lives on <see cref="DecayTier"/> (a
/// separate column). Character seeds carry <c>provenance=Facts</c>
/// AND <c>tier=Anchored</c>, so a single Facts-tier search returns
/// both regular Facts records and anchored foundation records in one
/// ranked list. The handler splits that list by
/// <see cref="DecayTier.Anchored"/> to populate the request's two
/// substrate fields (<c>MarkAssertedSubstrate</c> = non-anchored;
/// <c>CanonicalSubstrate</c> = anchored). The prompt shape is
/// unchanged — that's P.3.3 territory.
/// </summary>
public sealed class FrontierVerifierHandler : ICognitivePipelineHandler
{
    /// <summary>
    /// Top-N records to retrieve against the composed reply text. 20 is
    /// the P.3.2 starting target — large enough that a claim with any
    /// substrate support in the Facts pool surfaces, small enough that
    /// the verifier prompt stays bounded. Splits across regular Facts +
    /// anchored character seeds after retrieval (see class XML).
    /// </summary>
    private const int VerifierRetrievalTopK = 20;

    private readonly IFrontierVerifierClient            _client;
    private readonly IMemorySearch                      _search;
    private readonly AniOptions                         _options;
    private readonly ILogger<FrontierVerifierHandler>   _log;

    public FrontierVerifierHandler(
        IFrontierVerifierClient            client,
        IMemorySearch                      search,
        IOptions<AniOptions>               options,
        ILogger<FrontierVerifierHandler>   log)
    {
        _client  = client  ?? throw new ArgumentNullException(nameof(client));
        _search  = search  ?? throw new ArgumentNullException(nameof(search));
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

        var request = await BuildRequestAsync(ctx, ct).ConfigureAwait(false);

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

    // ─── Substrate retrieval (plan-doc §9.1 + P.3.2: post-hoc semantic) ──

    /// <summary>
    /// Build the verifier request. Substrate is retrieved POST-HOC by
    /// semantic similarity against the composed reply text (P.3.2,
    /// May 12 2026) so the verifier sees the records most relevant to
    /// the message the model is about to ship, not whatever the cycle's
    /// perception-keyed snapshot pools happened to contain.
    ///
    /// One <see cref="IMemorySearch.SearchByTierAsync"/> call against
    /// <see cref="EpistemicTier.Facts"/> returns both regular Facts
    /// records and anchored character seeds in a single ranked list
    /// (anchored records carry <c>provenance=Facts</c> AND
    /// <c>tier=Anchored</c>). The handler splits that list by
    /// <see cref="DecayTier.Anchored"/> so the existing two-field
    /// request shape (<c>MarkAssertedSubstrate</c> +
    /// <c>CanonicalSubstrate</c>) is preserved — verifier prompt
    /// changes are P.3.3 territory.
    ///
    /// <c>CharacterState.CanonicalContacts</c> still sources
    /// <c>KnownContacts</c> — that's identity scaffolding, not
    /// claim-grounding substrate.
    /// </summary>
    private async Task<FrontierVerifierRequest> BuildRequestAsync(
        CognitivePipelineContext ctx, CancellationToken ct)
    {
        var artifact     = ctx.Artifact;
        var character    = ctx.Snapshot?.CharacterState;
        var composedText = artifact.Content ?? string.Empty;

        // Post-hoc retrieval keyed to the composed reply. Same scoring
        // surface (cosine + importance + Feature 20 recency) the
        // composition pipeline uses. P.4 (May 12 2026): retrieval is
        // floored at MinCosineThresholdVerifier so only meaningful
        // similarity reaches Sonnet's q1–q5 evaluation — see the
        // AnthropicVerifierClient system-prompt clause for the
        // empty-substrate-after-filter interpretation contract.
        IReadOnlyList<ScoredMemory> factsHits;
        try
        {
            var results = await _search
                .SearchByTierAsync(
                    composedText, EpistemicTier.Facts, VerifierRetrievalTopK, ct,
                    minCosine: (float)_options.MinCosineThresholdVerifier)
                .ConfigureAwait(false);
            factsHits = results.ToList();
        }
        catch (Exception ex)
        {
            // Retrieval failure is non-fatal — render with empty
            // substrate and let the verifier work with what it has. The
            // graceful-degradation path in HandleAsync still catches a
            // verifier-side failure if one follows.
            _log.LogWarning(ex,
                "P_VERIFIER_RETRIEVAL_FAILED — continuing with empty substrate");
            factsHits = Array.Empty<ScoredMemory>();
        }

        // Split the single Facts-tier pool by DecayTier so the existing
        // two-field request shape is preserved. Anchored records
        // (foundation memories — character seeds, world layer canon) go
        // to CanonicalSubstrate; everything else goes to
        // MarkAssertedSubstrate.
        var markAsserted = RenderRecords(
            factsHits.Where(h => h.Record.DecayTier != DecayTier.Anchored)
                     .Select(h => h.Record));

        var canonical = RenderRecords(
            factsHits.Where(h => h.Record.DecayTier == DecayTier.Anchored)
                     .Select(h => h.Record));

        var addressee = !string.IsNullOrWhiteSpace(character?.PrimaryContactName)
            ? character!.PrimaryContactName
            : string.Empty;

        var knownContacts = character?.CanonicalContacts is { Count: > 0 } contacts
            ? string.Join(", ", contacts)
            : string.Empty;

        // Day-of-week reads the wall-clock value at the offset the producer
        // stored on the artifact (DateTimeOffset.Now bakes Mark's local TZ
        // into GeneratedAt). Use .DateTime, NOT .LocalDateTime: the latter
        // re-projects through the running machine's TZ, which silently flips
        // the day when the artifact's offset differs from the host's TZ
        // (e.g. UTC CI runner reading a CDT-offset artifact at 19:30 CDT
        // would see 00:30 UTC the next day and report Tuesday for Monday).
        var localTime = artifact.GeneratedAt.DateTime;

        return new FrontierVerifierRequest(
            ComposedMessage:        composedText,
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
