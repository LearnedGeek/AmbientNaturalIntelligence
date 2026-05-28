using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using LearnedGeek.ML.Interfaces;
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
    private readonly IStateStore _state;
    private readonly IMemoryPersistence _persist;
    private readonly IMemoryAnalytics _analytics;
    private readonly IOllamaClient _ollama;
    private readonly AniOptions _aniOptions;
    private readonly ITextClassificationService? _mlClassifier;
    private readonly IEmotionalSubstrateScorer? _substrateScorer;
    private readonly ILogger<EmotionalProcessor> _log;

    // Alignment map: which ML emotions correspond to which heuristic registers
    private static readonly Dictionary<string, HashSet<string>> EmotionRegisterAlignment = new()
    {
        ["happiness"] = ["Delight", "Playfulness", "Warmth"],
        ["sadness"] = ["Longing", "Hurt", "Existential", "Wistful"],
        ["anger"] = ["Hurt", "Frustration"],
        ["fear"] = ["Concern", "Existential"],
        ["neutral"] = ["Curiosity", "Existential", "Resilience", "Unclassified"],
        ["love"] = ["Tenderness", "Longing", "Warmth"],
        ["curiosity"] = ["Curiosity"],
        ["amusement"] = ["Playfulness", "Delight"],
        ["surprise"] = ["Curiosity", "Delight"],
        ["disgust"] = ["Frustration", "Hurt"],
    };

    public EmotionalProcessor(
        IStateStore state,
        IMemoryPersistence persist,
        IMemoryAnalytics analytics,
        IOllamaClient ollama,
        IOptions<AniOptions> aniOptions,
        ILogger<EmotionalProcessor> log,
        ITextClassificationService? mlClassifier = null,
        IEmotionalSubstrateScorer? substrateScorer = null)
    {
        _state = state;
        _persist = persist;
        _analytics = analytics;
        _ollama = ollama;
        _aniOptions = aniOptions.Value;
        _log = log;
        _mlClassifier = mlClassifier;
        _substrateScorer = substrateScorer;
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

            // Contribution 9 PR-2 (Issue #68) — shadow-mode substrate scoring.
            // EmoLLaMA-chat-7B substrate vector logged in parallel with the
            // discrete classifier above for paired-observation data. No behavior
            // change: the substrate vector is observed only, not yet consumed by
            // contribution composition. Migration of this consumer to substrate-
            // first is a separate follow-on PR.
            if (_substrateScorer is not null)
            {
                try
                {
                    var substrate = await _substrateScorer.ScoreAsync(content, ct).ConfigureAwait(false);
                    _log.LogInformation(
                        "Substrate shadow-score: schema={Schema} discreteRegister={Register} discreteSeverity={Severity:F2} substrate={Components}",
                        substrate.MeasurementSchema,
                        register,
                        severity,
                        JsonSerializer.Serialize(substrate.Components));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Substrate shadow-scoring failed; continuing with discrete classifier only");
                }
            }

            // Cap ambient thought severity at 0.85 — only real events (conversations,
            // hurt/care detection) should reach Global tier (threshold 0.98).
            // The scoring model inflates ambient thoughts to 0.95+ raw, which cubes
            // to 0.86+, hitting Global promotion and creating 12-hour contributions
            // that saturate Warmth. Capping at 0.85 keeps ambient thoughts in
            // Conversation tier (3h half-life) at most. Reserve Global for events
            // where a human is actually present.
            if (isAmbientCycle && severity > 0.85f)
                severity = 0.85f;

            // Skip if all zeros — no emotional impact.
            // SonarCloud S1244 — IEEE 754 float-equality is unreliable for arithmetic
            // results; here we deliberately want "all four exactly zero" semantics
            // (the per-dimension scorer assigns exact 0.0f when no change is
            // identified). A small-epsilon range matches the actual signal: any
            // delta below 1e-6 is below the cycle's reporting resolution anyway.
            const float ZeroEpsilon = 1e-6f;
            if (MathF.Abs(warmth) < ZeroEpsilon
                && MathF.Abs(energy) < ZeroEpsilon
                && MathF.Abs(worry) < ZeroEpsilon
                && MathF.Abs(playfulness) < ZeroEpsilon)
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

            var sourceContent = content.Length > 500 ? content[..500] : content;

            // Semantic dedup: if a similar thought already has an active contribution,
            // refresh it (update deltas, reset decay clock) instead of stacking.
            float[]? embedding = null;
            try { embedding = await _ollama.EmbedAsync(sourceContent, ct).ConfigureAwait(false); }
            catch { /* embedding failure is non-fatal — skip dedup */ }

            if (embedding is not null)
            {
                var existing = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);
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
                    match.Register = register;
                    match.CreatedAt = DateTimeOffset.UtcNow;
                    match.SourceContent = sourceContent;
                    await ClassifyWithMLAsync(match, sourceContent, ct).ConfigureAwait(false);
                    await _persist.SaveEmotionalContributionAsync(match, ct).ConfigureAwait(false);
                    _log.LogDebug("Refreshed existing emotional contribution (semantic match)");

                    var allContributions = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);
                    state.ComputeFromContributions(allContributions);
                    await _persist.SaveEmotionalStateAsync(state, ct).ConfigureAwait(false);

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
                Register = register,
            };

            await _persist.SaveEmotionalContributionAsync(contribution, ct).ConfigureAwait(false);

            // ML classification: run LM-Kit on the same content for dual-signal tracking
            await ClassifyWithMLAsync(contribution, sourceContent, ct).ConfigureAwait(false);

            // Recompute state from all active contributions
            var contributions = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);
            state.ComputeFromContributions(contributions);
            await _persist.SaveEmotionalStateAsync(state, ct).ConfigureAwait(false);

            _log.LogDebug("Emotional contribution ({Category}/{Register}, sev={Severity:F2}, halfLife={HalfLife:F1}h): W={Warmth:+0.00;-0.00} E={Energy:+0.00;-0.00} C={Worry:+0.00;-0.00} P={Playfulness:+0.00;-0.00} ML={MLEmotion}({MLConf:F2}) div={Div:F2}",
                effectiveCategory, register, severity, halfLife, warmth, energy, worry, playfulness,
                contribution.MLEmotion ?? "—", contribution.MLConfidence ?? 0f, contribution.DivergenceScore ?? 0f);
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
        await _persist.SaveEmotionalContributionAsync(contribution, ct).ConfigureAwait(false);

        var contributions = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);
        state.ComputeFromContributions(contributions);
        await _persist.SaveEmotionalStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Feature 2: Open loops create gentle worry pressure on the emotional state.
    /// Proportional to count and age of oldest loop. Max +0.15 worry contribution.
    /// </summary>
    public async Task ApplyOpenLoopPressureAsync(EmotionalState emotionalState, CancellationToken ct)
    {
        try
        {
            var openLoops = (await _analytics.GetOpenLoopsAsync(ct).ConfigureAwait(false)).ToList();
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

    /// <summary>
    /// Run LM-Kit classification on the contribution's source content and compute divergence.
    /// Non-fatal: if classification fails, the contribution keeps its heuristic values only.
    /// </summary>
    private async Task ClassifyWithMLAsync(EmotionalContribution contribution, string text, CancellationToken ct)
    {
        if (_mlClassifier is null || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var emotion = await _mlClassifier.ClassifyEmotionAsync(text, ct).ConfigureAwait(false);
            var sarcasm = await _mlClassifier.DetectSarcasmAsync(text, ct).ConfigureAwait(false);

            contribution.MLEmotion = emotion.PrimaryEmotion;
            contribution.MLConfidence = emotion.Confidence;
            contribution.MLSarcasmDetected = sarcasm.IsSarcastic;
            contribution.DivergenceScore = ComputeDivergence(emotion.PrimaryEmotion, contribution.Register);

            _log.LogDebug("ML classification: {Text} → {Emotion} ({Confidence:F2}), heuristic={Register}, divergence={Div:F2}",
                text.Length > 60 ? text[..60] + "..." : text,
                emotion.PrimaryEmotion, emotion.Confidence, contribution.Register, contribution.DivergenceScore);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "ML classification failed — contribution keeps heuristic values only");
        }
    }

    /// <summary>
    /// Compute divergence between ML emotion and heuristic register.
    /// 0.0 = aligned (emotion maps naturally to the register),
    /// 1.0 = fully divergent (no alignment mapping exists).
    /// </summary>
    private static float ComputeDivergence(string mlEmotion, string heuristicRegister)
    {
        if (string.IsNullOrEmpty(mlEmotion) || string.IsNullOrEmpty(heuristicRegister))
            return 0.5f; // Unknown — neutral divergence

        if (EmotionRegisterAlignment.TryGetValue(mlEmotion, out var alignedRegisters))
            return alignedRegisters.Contains(heuristicRegister) ? 0.0f : 1.0f;

        return 1.0f; // Unknown emotion — fully divergent
    }

    private static readonly HashSet<string> ValidRegisters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Longing", "Delight", "Playfulness", "Curiosity", "Desire",
        "Tenderness", "Existential", "Wistful", "Frustration"
    };

    /// <summary>
    /// Normalize register names: fix casing, extract primary from blended,
    /// and fall back to Unclassified for unknown values.
    /// </summary>
    private static string NormalizeRegister(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unclassified";

        // Handle blended registers: "primarily Longing, secondarily Tenderness" → "Longing"
        // Also handles "Longing+Curiosity", "Longing/Tenderness", etc.
        var cleaned = raw.Trim();

        // "primarily X" pattern
        if (cleaned.StartsWith("primarily ", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[10..].Split([',', '+', '/', ';'], 2)[0].Trim();

        // Split on common delimiters and take first
        cleaned = cleaned.Split([',', '+', '/', ';'], 2)[0].Trim();

        // Strip "secondarily" if it somehow ended up first
        if (cleaned.StartsWith("secondarily ", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[12..].Trim();

        // Match against valid registers (case-insensitive) and return canonical casing
        foreach (var valid in ValidRegisters)
        {
            if (string.Equals(cleaned, valid, StringComparison.OrdinalIgnoreCase))
                return valid;
        }

        return "Unclassified";
    }

    public (float warmth, float energy, float worry, float playfulness, string register, float severity)
        ParseEmotionalShift(string raw, float maxDelta = 0.2f)
    {
        try
        {
            var doc = JsonDocument.Parse(raw.Trim());
            var root = doc.RootElement;

            var rawRegister = root.TryGetProperty("register", out var regVal)
                ? regVal.GetString() ?? "Unclassified"
                : "Unclassified";

            // Normalize: take primary register from blended ("primarily Longing, secondarily Tenderness" → "Longing")
            // and fix case ("longing" → "Longing")
            var register = NormalizeRegister(rawRegister);

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
            return (0f, 0f, 0f, 0f, "Unclassified", 0.1f);
        }

        float ClampDelta(JsonElement root, string prop)
        {
            if (root.TryGetProperty(prop, out var val))
                return (float)Math.Clamp(val.GetDouble(), -maxDelta, maxDelta);
            return 0f;
        }
    }
}
