using System.Text.Json;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Utilities;
using AniRuntime.Core.Models;
using AniRuntime.Emergence.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Emergence;

/// <summary>
/// E1 emergence observer — passive observation only.
/// Scores each completed cognitive cycle, writes to the emergence log,
/// and accumulates resonance records for high-scoring cycles.
///
/// This observer receives immutable CycleObservation snapshots.
/// It cannot read or write the main runtime's database.
/// All data goes to the separate ani-emergence.db.
/// </summary>
public class EmergenceObserver : IEmergenceObserver
{
    private readonly EmergenceStore _store;
    private readonly ILogger<EmergenceObserver> _log;

    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.CamelCase;

    /// <summary>Cycles scoring above this threshold get a resonance record.</summary>
    private const float ResonanceThreshold = 0.4f;

    public EmergenceObserver(
        EmergenceStore store,
        ILogger<EmergenceObserver> log)
    {
        _store = store;
        _log   = log;
    }

    public async Task OnCycleCompleteAsync(CycleObservation observation, CancellationToken ct = default)
    {
        var score = ResonanceScorer.Score(observation);

        // Always log the scored cycle — this is the research instrument
        var logEntry = new EmergenceLogEntry
        {
            Timestamp            = observation.Timestamp,
            EntryType            = "CycleScored",
            ResonanceScore       = score,
            DetailsJson          = BuildDetailsJson(observation, score),
            CycleObservationJson = JsonSerializer.Serialize(observation, JsonOpts),
        };

        await _store.WriteLogEntryAsync(logEntry, ct).ConfigureAwait(false);

        // High-scoring cycles accumulate resonance
        if (score >= ResonanceThreshold && !string.IsNullOrEmpty(observation.InnerThought))
        {
            var theme = ExtractTheme(observation);
            var existing = await FindExistingResonanceAsync(theme, ct).ConfigureAwait(false);

            if (existing is not null)
            {
                // Bump existing resonance
                existing.ResonanceScore += score;
                existing.OccurrenceCount++;
                existing.LastObserved = observation.Timestamp;
                await _store.SaveResonanceAsync(existing, ct).ConfigureAwait(false);

                _log.LogDebug("Emergence: resonance bump \"{Theme}\" → {Score:F2} ({Count} occurrences)",
                    theme, existing.ResonanceScore, existing.OccurrenceCount);
            }
            else
            {
                // New resonance record
                var record = new ResonanceRecord
                {
                    Theme           = theme,
                    ResonanceScore  = score,
                    OccurrenceCount = 1,
                    FirstObserved   = observation.Timestamp,
                    LastObserved    = observation.Timestamp,
                    Type            = ClassifyResonanceType(observation),
                    Origin          = "Observed",
                };
                await _store.SaveResonanceAsync(record, ct).ConfigureAwait(false);

                _log.LogDebug("Emergence: new resonance \"{Theme}\" (score={Score:F2})", theme, score);
            }
        }

        _log.LogDebug("Emergence: cycle scored {Score:F2} (threshold={Threshold:F2})",
            score, ResonanceThreshold);
    }

    /// <summary>
    /// Extract a theme label from the cycle observation.
    /// Uses the register classification if available, falls back to a content summary.
    /// </summary>
    private static string ExtractTheme(CycleObservation obs)
    {
        // Register gives us the emotional category (e.g. "Longing", "Delight", "Mischief")
        if (!string.IsNullOrEmpty(obs.Register))
            return obs.Register;

        // Conversation cycles get a generic theme
        if (obs.WasConversationCycle)
            return "Conversation";

        // Outreach cycles
        if (obs.OutreachOutcome == "send")
            return "Outreach";

        // Silence
        if (obs.ChoseSilence)
            return "ChosenSilence";

        return "Ambient";
    }

    private static string ClassifyResonanceType(CycleObservation obs)
    {
        if (obs.WasConversationCycle || obs.DesireToConnect > 0.5f)
            return "Relational";

        if (obs.PerceptionSummaries.Count > 0)
            return "Aesthetic";

        return "Behavioral";
    }

    private async Task<ResonanceRecord?> FindExistingResonanceAsync(string theme, CancellationToken ct)
    {
        var records = await _store.GetResonanceRecordsAsync(100, ct).ConfigureAwait(false);
        return records.FirstOrDefault(r =>
            r.Theme.Equals(theme, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildDetailsJson(CycleObservation obs, float score)
    {
        var details = new
        {
            score,
            components = new
            {
                emotional  = ResonanceScorer.EmotionalMagnitude(obs),
                novelty    = ResonanceScorer.NoveltySignal(obs),
                outreach   = ResonanceScorer.OutreachQuality(obs),
                relational = ResonanceScorer.RelationalSignal(obs),
            },
            register    = obs.Register,
            severity    = obs.Severity,
            valence     = obs.RelationalValence,
            outcome     = obs.OutreachOutcome,
            conversation = obs.WasConversationCycle,
            silence     = obs.ChoseSilence,
        };
        return JsonSerializer.Serialize(details, JsonOpts);
    }
}
