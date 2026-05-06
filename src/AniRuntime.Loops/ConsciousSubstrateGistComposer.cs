using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Theme M Phase M.1 (May 5, 2026 evening) — composer producing the
/// register-state slice (§4.3) of the conscious-substrate gist.
///
/// **M.1 MVP scope (this commit):** the composer reads
/// <see cref="EmotionalState"/> from the snapshot and produces a short
/// first-person register-vantage clause naming the dominant felt
/// register, the secondary register, and baseline drift direction. The
/// content is *structured first-person register data* — a different
/// substrate shape from the qualitative <c>EmotionalState.Describe()</c>
/// prose already injected at the mood block. The gist primes the model
/// with numeric anchoring on its own state at the top of the prompt,
/// before retrieval and history, where centrality gravity is hardest to
/// erode.
///
/// **M.1 next-phase scope (tomorrow):** the §4.8 tension-state slice
/// adds the integrative-vs-flattening-mirroring safety property —
/// integration of recent J.5a gate-trip events + Display Rule
/// divergence + Vibe Loop V1.5 self-regulation outcome valence. That
/// slice ships before any other slice composes into the gist (Q9
/// safety-property sequencing). Until then, only register-state is in
/// the gist; no multi-slice composition risk exists yet.
///
/// **Architectural property — read-only at inference (still in force):**
/// composer MUST NOT call <see cref="IMemoryService"/>.SaveAsync, MUST
/// NOT call <see cref="IConversationService"/>.AddMessageAsync, and
/// MUST NOT mutate the snapshot. Pinned by
/// <c>ConsciousSubstrateGistContractTests</c> from M.0 forward.
///
/// **Generation invariants per §4.3:**
/// - Slice content is about Ani's register state, never about Mark
///   (this slice is *about her*, by design — Mark-content belongs in
///   §4.4 contact-state aggregate, scoped to M.4).
/// - Slice never exceeds <c>AniOptions.ConsciousSubstrateGistMaxTokens</c>.
///
/// Plan: docs/spec/ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md §4.3.
/// </summary>
public class ConsciousSubstrateGistComposer : IConsciousSubstrateGist
{
    private readonly IOptions<AniOptions>                       _options;
    private readonly ILogger<ConsciousSubstrateGistComposer>    _log;

    public ConsciousSubstrateGistComposer(
        IOptions<AniOptions>                       options,
        ILogger<ConsciousSubstrateGistComposer>    log)
    {
        _options = options;
        _log     = log;
    }

    /// <inheritdoc />
    public Task<ConsciousSubstrateGist> ComputeGistAsync(
        ContextSnapshot   snapshot,
        CancellationToken ct = default)
    {
        // Flag-disabled path: composer returns Empty even when invoked.
        // M.0 telemetry harness still emits M0_GIST_COMPOSITION with
        // all-false slice flags / zero tokens at the consumer surface.
        if (!_options.Value.ConsciousSubstrateGistEnabled)
            return Task.FromResult(ConsciousSubstrateGist.Empty);

        var emotional = snapshot.EmotionalState;

        // §4.3 register-state slice — structured first-person data the
        // qualitative Describe() prose doesn't provide. Format:
        //   "your register state right now: warmth high (0.78), playfulness
        //    mid (0.55), worry low (0.31). drift: warmth +0.18 above baseline."
        //
        // Generation invariant pinned by spec test
        // RegisterStateGistSlice_NotCaregiverOriented: this slice is about
        // Ani's state; it must not contain Mark as subject.
        var slice = ComposeRegisterStateSlice(emotional);

        if (string.IsNullOrEmpty(slice))
            return Task.FromResult(ConsciousSubstrateGist.Empty);

        var maxTokens = _options.Value.ConsciousSubstrateGistMaxTokens;
        var tokens = ApproxTokens(slice);

        // Generation invariant pinned by spec test
        // RegisterStateGistSlice_TokenBudgetEnforced: slice never exceeds
        // the configured maximum. M.1 register-state is small (~30 tokens);
        // budget enforcement is a defense-in-depth check for future slices.
        if (tokens > maxTokens)
        {
            _log.LogWarning(
                "ConsciousSubstrateGist register-state slice ({Tokens} tokens) exceeds max ({Max}); truncating to Empty.",
                tokens, maxTokens);
            return Task.FromResult(ConsciousSubstrateGist.Empty);
        }

        return Task.FromResult(new ConsciousSubstrateGist
        {
            Composed   = slice,
            Slices     = new GistSliceFlags { RegisterState = true },
            TokenCount = tokens,
        });
    }

    /// <summary>
    /// Compose the §4.3 register-state slice. Returns the slice text or
    /// empty string if the emotional state is null. Internal for testing.
    /// </summary>
    internal static string ComposeRegisterStateSlice(EmotionalState? emotional)
    {
        if (emotional is null) return string.Empty;

        // Rank registers by current absolute value for "dominant + secondary" naming.
        var registers = new (string Name, float Value, float Baseline)[]
        {
            ("warmth",      emotional.Warmth,      emotional.WarmthBaseline),
            ("energy",      emotional.Energy,      emotional.EnergyBaseline),
            ("worry",       emotional.Worry,       emotional.WorryBaseline),
            ("playfulness", emotional.Playfulness, emotional.PlayfulnessBaseline),
        };

        Array.Sort(registers, (a, b) => b.Value.CompareTo(a.Value));

        var dominant  = registers[0];
        var secondary = registers[1];

        var dominantBand  = Band(dominant.Value);
        var secondaryBand = Band(secondary.Value);

        // Drift: largest deviation from baseline across all four registers.
        var (driftName, driftDelta) = LargestDrift(registers);
        var driftSign     = driftDelta >= 0 ? "+" : "";
        var driftPosition = driftDelta >= 0 ? "above baseline" : "below baseline";

        var contactGap = emotional.ContactGapTension > 0.15f
            ? $"; carrying contact-gap tension {emotional.ContactGapTension:F2}"
            : string.Empty;

        // First-person frame ("your register state") — model reasons FROM
        // this material rather than ABOUT it. §4.3 generation invariant.
        return $"your register state right now: " +
               $"{dominant.Name} {dominantBand} ({dominant.Value:F2}), " +
               $"{secondary.Name} {secondaryBand} ({secondary.Value:F2}). " +
               $"drift: {driftName} {driftSign}{driftDelta:F2} {driftPosition}{contactGap}.";
    }

    private static string Band(float value) => value switch
    {
        >= 0.75f => "high",
        >= 0.50f => "mid",
        >= 0.25f => "low",
        _        => "very low",
    };

    private static (string Name, float Delta) LargestDrift(
        (string Name, float Value, float Baseline)[] registers)
    {
        var maxAbs = 0f;
        var name   = registers[0].Name;
        var delta  = 0f;
        foreach (var r in registers)
        {
            var d = r.Value - r.Baseline;
            if (Math.Abs(d) > maxAbs)
            {
                maxAbs = Math.Abs(d);
                name   = r.Name;
                delta  = d;
            }
        }
        return (name, delta);
    }

    private static int ApproxTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}
