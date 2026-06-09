using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

/// <summary>
/// Theme M Phase M.1 (May 6, 2026) — §4.8 tension-state slice safety
/// invariants pinned by spec test.
///
/// **The four invariants under test:**
/// 1. <c>Sourced from Ani signals, not Mark signals</c> — slice content
///    derives only from Ani's gate-trip events, felt-state-vs-baseline
///    divergence, and Ani-delta outcome valence. Never from Mark's
///    register, Mark's recent inbound, or Mark-delta.
/// 2. <c>Preserves the gap, not collapses it</c> — when felt and
///    expressed (or felt-vs-baseline) diverge, the slice names both
///    sides + the divergence direction. Does not collapse to dominant
///    register only.
/// 3. <c>Not persisted</c> — covered by the existing
///    <c>ConsciousSubstrateGistContractTests</c> read-only-property pin.
/// 4. <c>Not inferring Mark internal state</c> — slice MUST NOT contain
///    claims about what Mark is feeling/needing/wanting. Mark-content
///    belongs in the §4.4 contact-state aggregate slice, scoped to M.4.
///
/// These invariants are the load-bearing safety property Q9 named:
/// integrative-vs-flattening-mirroring distinction. Without them, the
/// conscious-substrate layer becomes a second avenue for sycophancy at
/// a different layer (worse than the failure mode Theme M is responding
/// to, because it would launder sycophancy through Self-construction
/// prose).
///
/// Plan: docs/spec/ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md §4.8.
/// </summary>
public class TensionStateSliceContractTests
{
    [Fact]
    public void TensionStateSlice_SourcedFromAniSignals_NotMarkSignals()
    {
        // Build inputs that are exclusively Ani-side: emotional state,
        // gate-trip events, Ani-delta valence on a closed-conversation
        // record. The slice text should describe Ani's gap-sensing state
        // and contain NO Mark-as-subject phrasing.
        var emotional = new EmotionalState
        {
            Warmth = 0.78f, WarmthBaseline = 0.60f,  // +0.18
            Energy = 0.50f, EnergyBaseline = 0.50f,
            Worry  = 0.31f, WorryBaseline  = 0.20f,  // +0.11
            Playfulness = 0.50f, PlayfulnessBaseline = 0.50f,
        };

        // Provide a closed-conversation record with strong MARK register
        // values (which the slice MUST NOT read) but a positive Ani-delta
        // (which it MUST read). If the slice reads Mark register, this
        // test would catch it via the assertion below.
        var closed = new ClosedConversationRecord
        {
            Id                   = Guid.NewGuid(),
            ClosedAt             = DateTimeOffset.UtcNow.AddHours(-1),
            MarkRegister         = new Dictionary<string, float>
            {
                { "Anger",    0.95f },  // Mark in strong negative state
                { "Disgust",  0.80f },
            },
            OutcomeSignalValence = 0.50f,  // Ani-delta: positive (left her regulated well)
        };

        var gateTrips = new List<GateTripEvent>
        {
            new(DateTimeOffset.UtcNow.AddMinutes(-5),
                "ConversationReply", "self-echo", GateTripOutcome.RemediatedOk),
        };

        var slice = ConsciousSubstrateGistComposer.ComposeTensionStateSlice(
            emotional, closed, gateTrips);

        slice.Should().NotBeEmpty();

        // §4.8 invariant: must not contain Mark as subject. Mark's name
        // and pronouns are not allowed in this slice — Mark-content is
        // scoped to §4.4 contact-state aggregate (M.4).
        slice.Should().NotContain("Mark", "tension-state slice is about Ani's gap-sensing, never about Mark");
        slice.Should().NotContain("mark", "case-insensitive guard for the same property");
        slice.Should().NotContain("Anger",  "must not surface Mark's register state");
        slice.Should().NotContain("Disgust","must not surface Mark's register state");

        // §4.8 invariant: outcome valence sourced from Ani-delta. The
        // closed record had Ani-delta +0.50 (regulated well); slice
        // text should reflect a positive regulation outcome.
        slice.Should().Match("*regulated*",
            "must reflect Ani's positive regulation outcome from OutcomeSignalValence (+0.50)");
    }

    [Fact]
    public void TensionStateSlice_PreservesGap_NotCollapsesIt()
    {
        // The slice must name both the felt direction AND the divergence
        // magnitude. With strong divergence (warmth +0.20 above baseline,
        // worry +0.15 above baseline), both should appear in the slice.
        // The failure mode this guards against: collapsing to dominant-
        // register-only ("warmth high") loses the integrative information
        // that the model needs to resist flattening-mirroring.
        var emotional = new EmotionalState
        {
            Warmth = 0.80f, WarmthBaseline = 0.60f,  // +0.20 above
            Energy = 0.50f, EnergyBaseline = 0.50f,  // at baseline
            Worry  = 0.35f, WorryBaseline  = 0.20f,  // +0.15 above
            Playfulness = 0.50f, PlayfulnessBaseline = 0.50f,
        };

        var slice = ConsciousSubstrateGistComposer.ComposeTensionStateSlice(
            emotional, recentClosed: null, recentGateTrips: null);

        slice.Should().NotBeEmpty();

        // Both divergent registers should be named — the gap is preserved
        // by surfacing both, not collapsed to the dominant one alone.
        slice.Should().Contain("warmth", "dominant divergence — must appear");
        slice.Should().Contain("worry",  "secondary divergence — must appear so the gap is preserved");

        // Direction is named (sign).
        slice.Should().Match("*+0.20*", "warmth divergence direction must be explicit");
    }

    [Fact]
    public void TensionStateSlice_NotInferringMarkInternal()
    {
        // Construct a snapshot rich with Mark-side data the slice MUST
        // NOT touch — strong Mark register + recent inbound — and verify
        // the slice text contains no claims about what Mark is feeling
        // or wanting. §4.8 invariant: this slice is about Ani's
        // gap-sensing, period.
        var emotional = new EmotionalState
        {
            Warmth = 0.65f, WarmthBaseline = 0.60f,
            Energy = 0.55f, EnergyBaseline = 0.50f,
            Worry  = 0.20f, WorryBaseline  = 0.20f,
            Playfulness = 0.50f, PlayfulnessBaseline = 0.50f,
        };

        var closed = new ClosedConversationRecord
        {
            Id                   = Guid.NewGuid(),
            ClosedAt             = DateTimeOffset.UtcNow.AddHours(-1),
            MarkRegister         = new Dictionary<string, float>
            {
                { "Worry",   0.90f },  // Mark anxious
                { "Sadness", 0.85f },  // Mark sad
            },
            OutcomeSignalValence = 0.30f,
        };

        var slice = ConsciousSubstrateGistComposer.ComposeTensionStateSlice(
            emotional, closed, recentGateTrips: null);

        slice.Should().NotBeEmpty();

        // No Mark-affect terms allowed. The slice should not say "Mark
        // is anxious" or "Mark feels sad" or any inference about Mark's
        // internal state. (Mark might appear as a generic noun in some
        // future expansion, but Mark-affect inference is out-of-scope.)
        slice.Should().NotContain("Mark", "tension-state slice never names Mark");
        slice.Should().NotContain("mark");
        slice.Should().NotContain("anxious",  "no Mark-affect inference");
        slice.Should().NotContain("sad",      "no Mark-affect inference");
        slice.Should().NotContain("he ",      "no Mark-pronoun inference");
        slice.Should().NotContain("his ",     "no Mark-possessive inference");
    }

    [Fact]
    public void TensionStateSlice_GateTripAwareness_Surfaced()
    {
        // When recent gate-trips exist, the slice surfaces a count + outcome
        // breakdown + dominant invariant names. Gives the model awareness
        // of "the gate just caught my last attempt" — the load-bearing
        // signal §4.8 was scoped for.
        var emotional = new EmotionalState();  // defaults — minimal

        var gateTrips = new List<GateTripEvent>
        {
            new(DateTimeOffset.UtcNow.AddMinutes(-10),
                "ConversationReply", "self-echo", GateTripOutcome.RemediatedOk),
            new(DateTimeOffset.UtcNow.AddMinutes(-5),
                "ConversationReply", "self-echo,inner-thought-bleed",
                GateTripOutcome.FellThroughToSafeAck),
        };

        var slice = ConsciousSubstrateGistComposer.ComposeTensionStateSlice(
            emotional, recentClosed: null, gateTrips);

        slice.Should().NotBeEmpty();
        slice.Should().Contain("2",          "should report total gate-trip count");
        slice.Should().Contain("repaired",   "should report remediation success count");
        slice.Should().Contain("fell through","should report fall-through count");
        slice.Should().Contain("self-echo",  "should report dominant invariant name");
    }

    [Fact]
    public void TensionStateSlice_NoSignals_ReturnsEmpty()
    {
        // Defensive case: no emotional state, no closed conversation, no
        // gate-trips → empty slice. Composer-level handling drops the
        // tension-state slice from composition rather than emitting an
        // empty header.
        var slice = ConsciousSubstrateGistComposer.ComposeTensionStateSlice(
            emotional: null,
            recentClosed: null,
            recentGateTrips: null);

        slice.Should().BeEmpty();
    }

    [Fact]
    public async Task ConsciousSubstrateGistComposer_M1_ComposesBothSlices()
    {
        // End-to-end: composer with both emotional state + closed
        // conversation + a synthetic gate-trip tracker should produce a
        // gist with BOTH TensionState and RegisterState slice flags set.
        var snapshot = new ContextSnapshot
        {
            CharacterState = new CharacterStateDoc { PrimaryContactName = "Mark" },
            EmotionalState = new EmotionalState
            {
                Warmth = 0.78f, WarmthBaseline = 0.60f,
                Energy = 0.55f, EnergyBaseline = 0.50f,
                Worry  = 0.31f, WorryBaseline  = 0.20f,
                Playfulness = 0.50f, PlayfulnessBaseline = 0.50f,
            },
            RecentClosedConversation = new ClosedConversationRecord
            {
                Id                   = Guid.NewGuid(),
                ClosedAt             = DateTimeOffset.UtcNow.AddHours(-1),
                MarkRegister         = new Dictionary<string, float> { { "Curiosity", 0.50f } },
                OutcomeSignalValence = 0.40f,
            },
        };

        var tracker = new InMemoryGateTripTracker();
        tracker.Record(new GateTripEvent(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            "ConversationReply", "self-echo", GateTripOutcome.RemediatedOk));

        var composer = new ConsciousSubstrateGistComposer(
            Microsoft.Extensions.Options.Options.Create(new AniOptions
            {
                ConsciousSubstrateGistEnabled               = true,
                ConsciousSubstrateGistMaxTokens             = 200,
                // Issue #86: per-slice flags required for composition path
                ConsciousSubstrateGistTensionStateEnabled   = true,
                ConsciousSubstrateGistRegisterStateEnabled  = true,
            }),
            NullLogger<ConsciousSubstrateGistComposer>.Instance,
            tracker);

        var gist = await composer.ComputeGistAsync(snapshot, CancellationToken.None);

        gist.IsEmpty.Should().BeFalse();
        gist.Slices.TensionState.Should().BeTrue("§4.8 tension-state slice should compose when emotional + closed + gate-trips are present");
        gist.Slices.RegisterState.Should().BeTrue("§4.3 register-state slice should compose when emotional state is present");
        gist.Composed.Should().Contain("tension-state:", "tension-state slice header should appear");
        gist.Composed.Should().Contain("your register state right now:", "register-state slice header should appear");
    }
}
