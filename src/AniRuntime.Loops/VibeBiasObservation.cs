using System.Text.Json;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Vibe Loop V1.5a (May 2, 2026) — observational telemetry helper.
/// Wraps <see cref="IVibeBiasService.ComputeBiasAsync"/> and logs
/// V15_BIAS_* lines per the V1.5a observation plan; the result is
/// NOT consumed by any prompt builder. V1.5b activation will replace
/// this call site with one that also feeds <c>SurfacedTopN</c> +
/// <c>RecommendedStrategyRegister</c> into composition prompts.
///
/// 2026-05-21 — Issue #46 adds structured-record persistence via
/// <see cref="IVibeBiasObservationStore"/> alongside the Serilog output,
/// so downstream consumers (V1.5b Gate 3 review, #43 effectiveness
/// matrix, #45 effectiveness substrate write-back) can join recommended-
/// register against <c>closed_conversation_records</c> by <c>thread_id</c>
/// without log-scraping. Persistence failure does not propagate — the
/// observational invariant (never affect dispatch) is preserved.
///
/// "Current Mark register" input comes from
/// <c>snapshot.RecentClosedConversation?.MarkRegister</c> — the cheapest
/// signal already in the cycle context. If null (cold-start / no recent
/// closed conversation), the call short-circuits and logs nothing —
/// V1.5a's observation requires a current-state vector to compare
/// against, and absence is itself a signal worth observing in the
/// substrate-volume metric.
///
/// <see cref="ClosedConversationRecord.AniRegister"/> on the next thread
/// close becomes the post-hoc "actual response register" surface;
/// dashboard joins by thread to render the divergence triple. We do
/// NOT call the LMKit register classifier here at composition time —
/// that would add an Ollama round-trip to every outreach + reply for
/// observational-only telemetry, which is the wrong trade.
/// </summary>
internal static class VibeBiasObservation
{
    /// <summary>
    /// Run the observational pass and emit telemetry. Always returns
    /// quickly; never throws — observational instrumentation MUST NOT
    /// affect dispatch semantics. Callers can fire-and-forget after a
    /// best-effort try/catch wrapper.
    ///
    /// <paramref name="observationStore"/> is optional (nullable) so the
    /// helper remains usable from tests and from any future call site
    /// that hasn't been wired through DI yet. When provided, one
    /// structured record is persisted per successful observation pass.
    ///
    /// <paramref name="threadId"/> is null for outreach call sites (no
    /// active thread at the V1.5a observation point) and the active
    /// thread id for reply call sites.
    /// </summary>
    public static async Task ObserveAsync(
        IVibeBiasService?           biasService,
        ContextSnapshot             snapshot,
        string                      callSite,
        ILogger                     log,
        CancellationToken           ct,
        IVibeBiasObservationStore?  observationStore = null,
        Guid?                       threadId         = null)
    {
        if (biasService is null) return;

        var closed = snapshot.RecentClosedConversation;
        if (closed is null)
        {
            log.LogDebug(
                "V15_BIAS_SKIP callSite={CallSite} reason=no-recent-closed-conversation",
                callSite);
            return;
        }

        if (closed.MarkRegister.Count == 0)
        {
            log.LogDebug(
                "V15_BIAS_SKIP callSite={CallSite} reason=empty-mark-register",
                callSite);
            return;
        }

        try
        {
            var ctx = new MarkRegisterContext(
                MarkRegister: VibeBiasService.ToOrderedVector(closed.MarkRegister),
                AsOf:         DateTimeOffset.UtcNow);

            var contactName = string.IsNullOrWhiteSpace(snapshot.CharacterState.PrimaryContactName)
                ? "Mark"
                : snapshot.CharacterState.PrimaryContactName;

            var result = await biasService.ComputeBiasAsync(
                contactName: contactName,
                contextNow:  ctx,
                ct:          ct).ConfigureAwait(false);

            EmitTelemetry(result, callSite, log);

            if (observationStore is not null)
            {
                await PersistAsync(
                    observationStore, result, callSite, contactName, threadId, log, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Observational-only — failures must not propagate into the
            // calling phase. Log at warning level so it shows up but
            // doesn't disrupt dispatch.
            log.LogWarning(
                ex, "V15_BIAS_FAILURE callSite={CallSite} — observational pass failed; dispatch continues",
                callSite);
        }
    }

    private static void EmitTelemetry(
        VibeBiasResult  result,
        string          callSite,
        ILogger         log)
    {
        var tierBreakdown = TierBreakdown(result.AllCandidates);
        log.LogInformation(
            "V15_BIAS_CANDIDATES callSite={CallSite} count={Count} tier_breakdown=light:{Light},medium:{Medium},heavy:{Heavy}",
            callSite, result.AllCandidates.Count,
            tierBreakdown.Light, tierBreakdown.Medium, tierBreakdown.Heavy);

        foreach (var c in result.SurfacedTopN)
        {
            log.LogInformation(
                "V15_BIAS_SURFACED callSite={CallSite} record={RecordId} weight={Weight:F3} tier={Tier} ageHours={AgeHours:F1} valence={Valence:+0.00;-0.00}",
                callSite, c.RecordId, c.BiasWeight, c.HalfLifeTier, c.AgeHours, c.OutcomeValence);
        }

        var topRegister = TopRegisterName(result.RecommendedStrategyRegister);
        log.LogInformation(
            "V15_BIAS_RECOMMENDED callSite={CallSite} top_register={TopRegister} top_value={TopValue:F2} surfaced_count={Count} reason={Reason}",
            callSite, topRegister.Name, topRegister.Value, result.SurfacedTopN.Count, result.DiversityScoreReason);
    }

    /// <summary>
    /// Issue #46 — persist one structured observation record. Wrapped in
    /// best-effort try/catch so SQLite failure can't propagate into
    /// dispatch. Logs <c>V15_BIAS_PERSIST_FAILURE</c> at warning level
    /// when persistence throws.
    /// </summary>
    private static async Task PersistAsync(
        IVibeBiasObservationStore   store,
        VibeBiasResult              result,
        string                      callSite,
        string                      contactName,
        Guid?                       threadId,
        ILogger                     log,
        CancellationToken           ct)
    {
        try
        {
            var tierBreakdown = TierBreakdown(result.AllCandidates);
            var topRegister   = TopRegisterName(result.RecommendedStrategyRegister);

            var surfacedJson = JsonSerializer.Serialize(
                result.SurfacedTopN.Select(c => new
                {
                    recordId  = c.RecordId,
                    weight    = c.BiasWeight,
                    tier      = c.HalfLifeTier,
                    ageHours  = c.AgeHours,
                    valence   = c.OutcomeValence,
                }));

            var record = new VibeBiasObservationRecord
            {
                Id                    = Guid.NewGuid(),
                ObservedAt            = DateTimeOffset.UtcNow,
                CallSite              = callSite,
                ThreadId              = threadId,
                ContactName           = contactName,
                CandidateCount        = result.AllCandidates.Count,
                TierBreakdownLight    = tierBreakdown.Light,
                TierBreakdownMedium   = tierBreakdown.Medium,
                TierBreakdownHeavy    = tierBreakdown.Heavy,
                SurfacedRecordsJson   = surfacedJson,
                RecommendedRegister   = topRegister.Name,
                RecommendedValue      = topRegister.Value,
                DiversityScoreReason  = result.DiversityScoreReason,
            };

            await store.SaveAsync(record, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex, "V15_BIAS_PERSIST_FAILURE callSite={CallSite} — structured record save failed; Serilog telemetry intact",
                callSite);
        }
    }

    private static (int Light, int Medium, int Heavy) TierBreakdown(
        IReadOnlyList<VibeBiasContribution> candidates)
    {
        var light  = 0;
        var medium = 0;
        var heavy  = 0;
        foreach (var c in candidates)
        {
            switch (c.HalfLifeTier)
            {
                case "Light":  light++;  break;
                case "Medium": medium++; break;
                case "Heavy":  heavy++;  break;
            }
        }
        return (light, medium, heavy);
    }

    private static (string Name, float Value) TopRegisterName(float[] vector)
    {
        if (vector.Length != ClosedConversationSummarizer.Registers.Count)
            return ("Unknown", 0f);

        var topIdx = 0;
        for (var i = 1; i < vector.Length; i++)
        {
            if (vector[i] > vector[topIdx]) topIdx = i;
        }
        return (ClosedConversationSummarizer.Registers[topIdx], vector[topIdx]);
    }
}
