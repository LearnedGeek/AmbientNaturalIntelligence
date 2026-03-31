namespace AniRuntime.Core.Models;

/// <summary>
/// Immutable snapshot of a completed cognitive cycle, published to the emergence layer.
/// Contains everything the emergence observer needs — no additional database queries required.
/// The cognitive cycle constructs this from data already in memory at the end of RunAsync.
/// </summary>
public record CycleObservation
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    // ── Inner thought ──────────────────────────────────────────────────────
    public string? InnerThought { get; init; }
    public string? Reflection { get; init; }
    public float RelationalValence { get; init; }

    // ── Emotional state snapshot ────────────────────────────────────────────
    public float Warmth { get; init; }
    public float Energy { get; init; }
    public float Worry { get; init; }
    public float Playfulness { get; init; }

    // ── Emotional shift this cycle ──────────────────────────────────────────
    public float WarmthDelta { get; init; }
    public float EnergyDelta { get; init; }
    public float WorryDelta { get; init; }
    public float PlayfulnessDelta { get; init; }
    public float Severity { get; init; }
    public string? Register { get; init; }

    // ── ML classification (dual-signal) ──────────────────────────────────────
    public string? MLEmotion { get; init; }
    public float? MLConfidence { get; init; }
    public float? DivergenceScore { get; init; }

    // ── Desire ──────────────────────────────────────────────────────────────
    public float DesireToConnect { get; init; }
    public bool DesireThresholdCrossed { get; init; }

    // ── Outreach outcome ────────────────────────────────────────────────────
    /// <summary>"send", "suppress-coherence", "suppress-gates", "no-desire", "withdrawn", or null (conversation mode).</summary>
    public string? OutreachOutcome { get; init; }
    public string? OutreachMessage { get; init; }
    public string? CoherenceGateDoor { get; init; }

    // ── Conversation (if in conversation mode) ──────────────────────────────
    public bool WasConversationCycle { get; init; }
    public string? ContactMessage { get; init; }
    public string? ReplyMessage { get; init; }

    // ── Silence ─────────────────────────────────────────────────────────────
    public bool ChoseSilence { get; init; }

    // ── Perceptions consumed ────────────────────────────────────────────────
    public IReadOnlyList<string> PerceptionSummaries { get; init; } = Array.Empty<string>();
}
