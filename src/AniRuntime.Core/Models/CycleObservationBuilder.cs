using System.Diagnostics;

namespace AniRuntime.Core.Models;

/// <summary>
/// CS3: Replaces the 17 local variables (obsThought, obsReflection, obsRegister, etc.)
/// scattered across CognitiveCycleProcessor.RunAsync. Each phase sets properties on the
/// builder, then Build() produces the immutable CycleObservation for the emergence layer.
/// </summary>
public class CycleObservationBuilder
{
    // Inner thought
    public string? InnerThought { get; set; }
    public string? Reflection { get; set; }
    public float RelationalValence { get; set; }
    public string? Register { get; set; }

    // Emotional state snapshot
    public EmotionalState? EmotionalState { get; set; }

    // Emotional shift deltas
    public float WarmthDelta { get; set; }
    public float EnergyDelta { get; set; }
    public float WorryDelta { get; set; }
    public float PlayfulnessDelta { get; set; }
    public float Severity { get; set; }

    // Desire
    public float DesireToConnect { get; set; }
    public bool DesireThresholdCrossed { get; set; }

    // Outreach
    public string? OutreachOutcome { get; set; }
    public string? OutreachMessage { get; set; }
    public string? CoherenceGateDoor { get; set; }

    // Conversation
    public bool WasConversationCycle { get; set; }
    public string? ContactMessage { get; set; }
    public string? ReplyMessage { get; set; }
    public bool ChoseSilence { get; set; }

    // ML classification (dual-signal)
    public string? MLEmotion { get; set; }
    public float? MLConfidence { get; set; }
    public float? DivergenceScore { get; set; }

    // Perceptions
    public List<string> PerceptionSummaries { get; set; } = new();

    public CycleObservation Build()
    {
        // Audit Root A (#197, 2026-11-27) — debug-build invariants on the
        // newly-wired fields. A plain "WasConversationCycle → ReplyMessage"
        // assertion from the issue acceptance list would false-positive on
        // legitimate silence paths (terminal message, silence heuristic,
        // empty-reply suppression). The contrapositives below catch
        // cross-wire bugs (a reply set without a conversation flag, or an
        // outreach message co-existing with a gate-door suppression label)
        // without flagging legitimate silence outcomes.
        Debug.Assert(ReplyMessage is null || WasConversationCycle,
            "CycleObservationBuilder: ReplyMessage set outside WasConversationCycle — upstream mis-wire.");
        Debug.Assert(OutreachMessage is null || CoherenceGateDoor is null,
            "CycleObservationBuilder: OutreachMessage set together with CoherenceGateDoor — pipeline should only populate one.");

        return new CycleObservation
        {
            InnerThought           = InnerThought,
            Reflection             = Reflection,
            RelationalValence      = RelationalValence,
            Warmth                 = EmotionalState?.Warmth ?? 0,
            Energy                 = EmotionalState?.Energy ?? 0,
            Worry                  = EmotionalState?.Worry ?? 0,
            Playfulness            = EmotionalState?.Playfulness ?? 0,
            WarmthDelta            = WarmthDelta,
            EnergyDelta            = EnergyDelta,
            WorryDelta             = WorryDelta,
            PlayfulnessDelta       = PlayfulnessDelta,
            Severity               = Severity,
            Register               = Register,
            DesireToConnect        = DesireToConnect,
            DesireThresholdCrossed = DesireThresholdCrossed,
            OutreachOutcome        = OutreachOutcome,
            OutreachMessage        = OutreachMessage,
            CoherenceGateDoor      = CoherenceGateDoor,
            WasConversationCycle   = WasConversationCycle,
            ContactMessage         = ContactMessage,
            ReplyMessage           = ReplyMessage,
            ChoseSilence           = ChoseSilence,
            MLEmotion              = MLEmotion,
            MLConfidence           = MLConfidence,
            DivergenceScore        = DivergenceScore,
            PerceptionSummaries    = PerceptionSummaries,
        };
    }
}
