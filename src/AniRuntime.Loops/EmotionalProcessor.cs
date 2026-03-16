using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Handles emotional state computation: LLM-scored shifts, direct contributions,
/// open-loop pressure, and emotional shift parsing. Each thought or event creates
/// an EmotionalContribution that decays independently via its half-life.
/// </summary>
public class EmotionalProcessor
{
    private readonly IMemoryService _memory;
    private readonly IOllamaClient _ollama;
    private readonly AniOptions _aniOptions;
    private readonly ILogger<EmotionalProcessor> _log;

    public EmotionalProcessor(
        IMemoryService memory,
        IOllamaClient ollama,
        IOptions<AniOptions> aniOptions,
        ILogger<EmotionalProcessor> log)
    {
        _memory = memory;
        _ollama = ollama;
        _aniOptions = aniOptions.Value;
        _log = log;
    }

    /// <summary>
    /// Scores emotional shift from a thought, conversation reply, or event, then
    /// creates an EmotionalContribution that decays over time via its half-life.
    /// Replaces the old model where deltas were applied permanently.
    /// </summary>
    public async Task ApplyEmotionalShiftAsync(
        EmotionalState state, string content, CancellationToken ct,
        float maxDelta = 0.2f, bool isAmbientCycle = false,
        ImpactCategory category = ImpactCategory.Ambient)
    {
        try
        {
            var (catMaxDelta, _) = ImpactCategoryDefaults.GetDefaults(category);
            var effectiveMax = Math.Min(maxDelta, catMaxDelta);

            var prompt = PromptBuilder.BuildEmotionalShiftPrompt(content, state, effectiveMax, isAmbientCycle);
            var raw = await _ollama.ChatJsonAsync(
                prompt.System, Array.Empty<ChatMessage>(), prompt.User, ct)
                .ConfigureAwait(false);

            var (warmth, energy, worry, playfulness, register, severity) = ParseEmotionalShift(raw, effectiveMax);

            // Skip if all zeros — no emotional impact
            if (warmth == 0f && energy == 0f && worry == 0f && playfulness == 0f)
                return;

            // Tier promotion: high-severity thoughts promote to longer-lasting tiers
            var effectiveCategory = ImpactCategoryDefaults.DetermineEffectiveTier(category, severity, _aniOptions);
            var (_, halfLife) = ImpactCategoryDefaults.GetDefaults(effectiveCategory);

            if (effectiveCategory != category)
            {
                _log.LogDebug("Tier promotion: {Base} → {Effective} (severity={Severity:F2})",
                    category, effectiveCategory, severity);
            }

            // C3 Associative Spark: flag for outreach if register is Curiosity and content is contact-related
            var isOutreachReady = string.Equals(register, "Curiosity", StringComparison.OrdinalIgnoreCase)
                && warmth > 0.05f; // warmth > 0.05 implies the connecting element involves the contact

            var sourceContent = content.Length > 200 ? content[..200] : content;

            // Semantic dedup: if a similar thought already has an active contribution,
            // refresh it (update deltas, reset decay clock) instead of stacking.
            float[]? embedding = null;
            try { embedding = await _ollama.EmbedAsync(sourceContent, ct).ConfigureAwait(false); }
            catch { /* embedding failure is non-fatal — skip dedup */ }

            if (embedding is not null)
            {
                var existing = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
                var match = existing.FirstOrDefault(e =>
                    e.Embedding is not null &&
                    VectorMath.CosineSimilarity(embedding, e.Embedding) > 0.85f);

                if (match is not null)
                {
                    // Refresh: update deltas and reset decay clock
                    match.WarmthDelta = warmth;
                    match.EnergyDelta = energy;
                    match.WorryDelta = worry;
                    match.PlayfulnessDelta = playfulness;
                    match.Severity = severity;
                    match.IsOutreachReady = isOutreachReady;
                    match.CreatedAt = DateTimeOffset.UtcNow;
                    match.SourceContent = sourceContent;
                    await _memory.SaveEmotionalContributionAsync(match, ct).ConfigureAwait(false);
                    _log.LogDebug("Refreshed existing emotional contribution (semantic match)");

                    var allContributions = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
                    state.ComputeFromContributions(allContributions);
                    await _memory.SaveEmotionalStateAsync(state, ct).ConfigureAwait(false);

                    _log.LogDebug("Emotional contribution refreshed ({Category}/{Register}, sev={Severity:F2}, halfLife={HalfLife:F1}h): W={Warmth:+0.00;-0.00} E={Energy:+0.00;-0.00} C={Worry:+0.00;-0.00} P={Playfulness:+0.00;-0.00}",
                        category, register, severity, halfLife, warmth, energy, worry, playfulness);
                    return;
                }
            }

            var contribution = new EmotionalContribution
            {
                SourceContent = sourceContent,
                WarmthDelta = warmth,
                EnergyDelta = energy,
                WorryDelta = worry,
                PlayfulnessDelta = playfulness,
                HalfLifeHours = halfLife,
                Category = effectiveCategory,
                Embedding = embedding,
                Severity = severity,
                IsOutreachReady = isOutreachReady,
            };

            await _memory.SaveEmotionalContributionAsync(contribution, ct).ConfigureAwait(false);

            // Recompute state from all active contributions
            var contributions = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
            state.ComputeFromContributions(contributions);
            await _memory.SaveEmotionalStateAsync(state, ct).ConfigureAwait(false);

            _log.LogDebug("Emotional contribution ({Category}/{Register}, sev={Severity:F2}, halfLife={HalfLife:F1}h): W={Warmth:+0.00;-0.00} E={Energy:+0.00;-0.00} C={Worry:+0.00;-0.00} P={Playfulness:+0.00;-0.00}",
                effectiveCategory, register, severity, halfLife, warmth, energy, worry, playfulness);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Emotional shift scoring failed — continuing with current state");
        }
    }

    /// <summary>
    /// Create a contribution with known deltas (not LLM-scored). Used for care detection,
    /// hurt detection, and other deterministic emotional events.
    /// </summary>
    public async Task SaveDirectContributionAsync(
        EmotionalState state, string source,
        float warmth, float energy, float worry, float playfulness,
        ImpactCategory category, CancellationToken ct)
    {
        var (_, halfLife) = ImpactCategoryDefaults.GetDefaults(category);
        var contribution = new EmotionalContribution
        {
            SourceContent = source,
            WarmthDelta = warmth,
            EnergyDelta = energy,
            WorryDelta = worry,
            PlayfulnessDelta = playfulness,
            HalfLifeHours = halfLife,
            Category = category,
        };
        await _memory.SaveEmotionalContributionAsync(contribution, ct).ConfigureAwait(false);

        var contributions = await _memory.GetActiveContributionsAsync(ct).ConfigureAwait(false);
        state.ComputeFromContributions(contributions);
        await _memory.SaveEmotionalStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Feature 2: Open loops create gentle worry pressure on the emotional state.
    /// Proportional to count and age of oldest loop. Max +0.15 worry contribution.
    /// </summary>
    public async Task ApplyOpenLoopPressureAsync(EmotionalState emotionalState, CancellationToken ct)
    {
        try
        {
            var openLoops = (await _memory.GetOpenLoopsAsync(ct).ConfigureAwait(false)).ToList();
            if (openLoops.Count == 0) return;

            var oldestAgeHours = (DateTimeOffset.UtcNow - openLoops.Min(l => l.CreatedAt)).TotalHours;
            var pressure = (float)Math.Min(openLoops.Count * 0.02 + oldestAgeHours * 0.005, 0.15);

            if (pressure > 0.005f)
            {
                // Create an ambient-tier contribution for open loop worry
                await SaveDirectContributionAsync(emotionalState,
                    $"open loop pressure — {openLoops.Count} unresolved threads",
                    warmth: 0f, energy: 0f, worry: pressure, playfulness: 0f,
                    ImpactCategory.Ambient, ct).ConfigureAwait(false);
                _log.LogDebug("Open loop pressure: +{Pressure:F3} worry ({Count} loops, oldest {Hours:F0}h)",
                    pressure, openLoops.Count, oldestAgeHours);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Open loop pressure calculation failed — continuing without");
        }
    }

    public (float warmth, float energy, float worry, float playfulness, string register, float severity)
        ParseEmotionalShift(string raw, float maxDelta = 0.2f)
    {
        try
        {
            var doc = JsonDocument.Parse(raw.Trim());
            var root = doc.RootElement;

            var register = root.TryGetProperty("register", out var regVal)
                ? regVal.GetString() ?? "Wistful"
                : "Wistful";

            var rawSeverity = root.TryGetProperty("severity", out var sevVal)
                ? (float)Math.Clamp(sevVal.GetDouble(), 0.0, 1.0)
                : 0.1f;  // missing field = routine thought, not defining moment

            // Recalibrate: the 8B scoring model consistently inflates severity
            // (routine musings score 0.90+ when they should be 0.1–0.3).
            // Apply a cubic curve that compresses the upper range:
            //   model 0.90 → effective 0.73, model 0.80 → effective 0.51,
            //   model 0.98 → effective 0.94, model 0.60 → effective 0.22
            // This preserves ordering while making Global promotion (≥0.85) genuinely rare
            // and Conversation promotion (≥0.70) reserved for notable events.
            var severity = rawSeverity * rawSeverity * rawSeverity;

            return (
                ClampDelta(root, "warmth"),
                ClampDelta(root, "energy"),
                ClampDelta(root, "worry"),
                ClampDelta(root, "playfulness"),
                register,
                severity
            );
        }
        catch
        {
            _log.LogDebug("Emotional shift parse failure: {Raw}", raw);
            return (0f, 0f, 0f, 0f, "Wistful", 0.1f);
        }

        float ClampDelta(JsonElement root, string prop)
        {
            if (root.TryGetProperty(prop, out var val))
                return (float)Math.Clamp(val.GetDouble(), -maxDelta, maxDelta);
            return 0f;
        }
    }
}
