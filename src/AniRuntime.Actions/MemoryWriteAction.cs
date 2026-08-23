using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Actions;

/// <summary>
/// Allows Ani to write directly to her own memory as an outreach action.
/// Used when the decision is to note something internally rather than send a message.
/// </summary>
public class MemoryWriteAction : IAniAction
{
    private readonly IMemoryService          _memory;
    private readonly ILogger<MemoryWriteAction> _log;

    public string ActionType => ActionTypes.Memory;

    public MemoryWriteAction(IMemoryService memory, ILogger<MemoryWriteAction> log)
    {
        _memory = memory;
        _log    = log;
    }

    public async Task<bool> ExecuteAsync(OutreachDecision decision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decision.Message)) return false;

        // F-2 Phase 1 P6 (2026-08-22) — MemoryWriteAction is Ani noting
        // something internally, always Ani-authored + verified.
        var noteTime = DateTimeOffset.UtcNow;
        var noteAttribution = AniRuntime.Core.Models.AttributionTriple.AniAt(noteTime);
        var noteRecord = new MemoryRecord
        {
            Type       = MemoryType.InnerThought,
            Content    = decision.Message,
            Importance = decision.Confidence,
            OccurredAt = noteTime,
            // Epistemic Grounding (Apr 10): MemoryWriteAction is used when Ani
            // chooses to note something internally rather than send a message.
            // Always Interior tier — this is her self-model being updated.
            Provenance = EpistemicTier.Interior,
            AttributedTo               = noteAttribution.AttributedTo,
            AttributedAt               = noteAttribution.AttributedAt,
            AttributedSourceRecordId   = noteAttribution.SourceRecordId,
            AttributedSourceDescriptor = noteAttribution.SourceDescriptor,
            AttributionTrust           = noteAttribution.Trust,
        };
        await _memory.SaveAsync(noteRecord, ct).ConfigureAwait(false);
        noteRecord.LogAttribution(_log);

        _log.LogDebug("MemoryWriteAction saved: {Content}", decision.Message[..Math.Min(80, decision.Message.Length)]);
        return true;
    }
}
