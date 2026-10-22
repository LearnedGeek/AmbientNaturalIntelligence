using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Outreach;

/// <summary>
/// F-4 Phase 2.1 implementation of <see cref="IOutreachSeedSelector"/>.
/// Scores the last N inner thoughts by
/// <c>Importance × (1 + AnchorBonus)</c> (via
/// <see cref="OutreachSeedScoring.Score"/>) and returns the top-scored
/// thought's content, with the just-generated thought as both a
/// candidate and the fallback.
///
/// <para>
/// Pool size and anchor bonus are configured via
/// <see cref="AniOptions.OutreachSeedCandidatePoolSize"/> and
/// <see cref="AniOptions.OutreachSeedAnchorBonus"/>. Gated on
/// <see cref="AniOptions.OutreachSeedAnchorBearingSelectionEnabled"/>;
/// when disabled, returns the just-generated thought unchanged.
/// </para>
/// </summary>
public sealed class AnchorBearingOutreachSeedSelector : IOutreachSeedSelector
{
    private readonly IMemorySearch                                   _search;
    private readonly AniOptions                                      _options;
    private readonly ILogger<AnchorBearingOutreachSeedSelector>      _log;

    public AnchorBearingOutreachSeedSelector(
        IMemorySearch                                   search,
        IOptions<AniOptions>                            options,
        ILogger<AnchorBearingOutreachSeedSelector>      log)
    {
        _search  = search  ?? throw new ArgumentNullException(nameof(search));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<string> SelectAsync(
        string justGeneratedThought,
        CharacterStateDoc characterState,
        CancellationToken ct)
    {
        if (!_options.OutreachSeedAnchorBearingSelectionEnabled)
        {
            return justGeneratedThought;
        }
        if (characterState is null) return justGeneratedThought;

        var poolSize = Math.Max(1, _options.OutreachSeedCandidatePoolSize);
        var bonus    = Math.Max(0f, _options.OutreachSeedAnchorBonus);

        IEnumerable<MemoryRecord> recent;
        try
        {
            recent = await _search.GetByTypeAsync(MemoryType.InnerThought, poolSize, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // Fail-open: outreach cycle has survived without this selector
            // for 7 months; a transient persistence fault never degrades
            // below pre-Phase-2.1 baseline (just-generated thought).
            _log.LogWarning(ex,
                "F4_OUTREACH_SEED_SELECT retrieval failed — falling back to just-generated thought");
            return justGeneratedThought;
        }

        var candidates = recent?.ToList() ?? new List<MemoryRecord>();

        // Score the just-generated thought at an implied Importance 0.5 if
        // it isn't yet persisted (first cycle, race against metadata write).
        // Most of the time it IS persisted and will appear in the recent
        // pool with its actual Importance.
        var bestContent  = justGeneratedThought;
        var bestScore    = OutreachSeedScoring.Score(
            justGeneratedThought,
            FindPersistedImportance(candidates, justGeneratedThought) ?? 0.5f,
            characterState,
            bonus);

        foreach (var record in candidates)
        {
            if (string.IsNullOrWhiteSpace(record.Content)) continue;
            if (string.Equals(record.Content, justGeneratedThought, StringComparison.Ordinal))
            {
                // Already scored above as the fallback candidate.
                continue;
            }

            var score = OutreachSeedScoring.Score(
                record.Content, record.Importance, characterState, bonus);

            if (score > bestScore)
            {
                bestScore   = score;
                bestContent = record.Content;
            }
        }

        if (!ReferenceEquals(bestContent, justGeneratedThought)
            && !string.Equals(bestContent, justGeneratedThought, StringComparison.Ordinal))
        {
            _log.LogInformation(
                "F4_OUTREACH_SEED_SELECT upgraded seed — score={Score:F2} pool={PoolSize} preview={Preview}",
                bestScore, candidates.Count,
                bestContent.Length > 80 ? bestContent[..80] : bestContent);
        }
        else
        {
            _log.LogDebug(
                "F4_OUTREACH_SEED_SELECT kept just-generated seed — score={Score:F2} pool={PoolSize}",
                bestScore, candidates.Count);
        }

        return bestContent;
    }

    private static float? FindPersistedImportance(
        IReadOnlyList<MemoryRecord> candidates, string thoughtContent)
    {
        foreach (var record in candidates)
        {
            if (string.Equals(record.Content, thoughtContent, StringComparison.Ordinal))
                return record.Importance;
        }
        return null;
    }
}
