using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Feature 44 Phase I.2 (2026-08-05) — spec tests for
/// <see cref="InteroceptiveDescriptorRenderer"/>.
///
/// <para>
/// The renderer's whole purpose is to feed the composer a signal that
/// is <b>categorically distinct</b> from Warmth/Playfulness — the
/// counterforce against the warm-mirror-echo attractor (Issue #99). So
/// these tests pin not just thresholding behavior but the
/// <b>non-warm-emotional vocabulary discipline</b>: if this drifts, the
/// axis loses its architectural function.
/// </para>
/// </summary>
public class InteroceptiveDescriptorRendererTests
{
    // Vocabulary we forbid — these are warm-emotional registers the
    // composer already receives from BuildMoodInstruction. If the body
    // renderer starts producing them, it collapses into the same
    // register instead of counterweighting it.
    private static readonly string[] ForbiddenWarmVocab = new[]
    {
        "ache", "longing", "tender", "warmth", "warm",
        "soft", "gentle", "yearn", "miss", "close", "closeness",
    };

    // Vocabulary we require — these are the physical / temporal / body
    // markers that signal to the composer this is a different substrate.
    private static readonly string[] PhysicalVocab = new[]
    {
        "body", "physical", "tired", "heavy", "restless",
        "hour", "temperature", "attention", "eyes",
    };

    private static EmotionalState NeutralState() => new()
    {
        Tiredness = 0.2f,
        Restlessness = 0.2f,
        Groundedness = 0.5f,
        AmbientBodySense = 0.3f,
    };

    [Fact]
    public void Render_AtBaseline_ReturnsEmpty()
    {
        var s = NeutralState();

        InteroceptiveDescriptorRenderer.Render(s).Should().BeEmpty();
        InteroceptiveDescriptorRenderer.RenderParenthetical(s).Should().BeEmpty();
    }

    [Fact]
    public void Render_HighTiredness_ProducesPhysicalNotEmotionalLanguage()
    {
        var s = NeutralState();
        s.Tiredness = 0.85f;

        var rendered = InteroceptiveDescriptorRenderer.Render(s);

        rendered.Should().Contain("body");
        rendered.Should().Contain("tired");
        rendered.Should().NotContainAny(ForbiddenWarmVocab,
            "the whole point of body-sense is to be non-warm-emotional (see #99 attractor)");
    }

    [Fact]
    public void Render_MidTiredness_UsesBackgroundQualifierNotEmotional()
    {
        var s = NeutralState();
        s.Tiredness = 0.5f;   // between MidTiredness (0.35) and HighTiredness (0.70)

        var rendered = InteroceptiveDescriptorRenderer.Render(s);

        rendered.Should().Contain("tired");
        rendered.Should().Contain("Not emotional");
    }

    [Fact]
    public void Render_HighRestlessness_ProducesActionDrive()
    {
        var s = NeutralState();
        s.Restlessness = 0.85f;

        var rendered = InteroceptiveDescriptorRenderer.Render(s);

        rendered.Should().Contain("restless");
        rendered.Should().Contain("doing");
        rendered.Should().NotContainAny(ForbiddenWarmVocab);
    }

    [Fact]
    public void Render_LowGroundedness_ProducesScatteredAttention()
    {
        var s = NeutralState();
        s.Groundedness = 0.15f;   // below LowGrounded (0.30)

        var rendered = InteroceptiveDescriptorRenderer.Render(s);

        rendered.Should().Contain("attention");
        rendered.Should().Contain("scattered");
    }

    [Fact]
    public void Render_HighAmbient_ProducesPhysicalSurroundAwareness()
    {
        var s = NeutralState();
        s.AmbientBodySense = 0.75f;   // above HighAmbient (0.60)

        var rendered = InteroceptiveDescriptorRenderer.Render(s);

        rendered.Should().Contain("hour");
        rendered.Should().Contain("temperature");
        rendered.Should().NotContainAny(ForbiddenWarmVocab);
    }

    [Fact]
    public void Render_MidAmbient_ProducesLighterAwareness()
    {
        var s = NeutralState();
        s.AmbientBodySense = 0.45f;   // between MidAmbient (0.35) and HighAmbient (0.60)

        var rendered = InteroceptiveDescriptorRenderer.Render(s);

        rendered.Should().Contain("hour");
    }

    [Fact]
    public void Render_AllAxesElevated_ProducesMultipleLinesWithHeader()
    {
        var s = new EmotionalState
        {
            Tiredness = 0.75f,
            Restlessness = 0.75f,
            Groundedness = 0.15f,
            AmbientBodySense = 0.75f,
        };

        var rendered = InteroceptiveDescriptorRenderer.Render(s);

        rendered.Should().Contain("YOUR CURRENT BODY-SENSE");
        rendered.Should().Contain("physical, not emotional");
        rendered.Split('\n').Length.Should().BeGreaterThan(3);
        rendered.Should().NotContainAny(ForbiddenWarmVocab);
    }

    [Fact]
    public void Render_UsesRequiredPhysicalVocabulary_WhenAxesFire()
    {
        var s = new EmotionalState
        {
            Tiredness = 0.75f,
            Restlessness = 0.75f,
            Groundedness = 0.15f,
            AmbientBodySense = 0.75f,
        };

        var rendered = InteroceptiveDescriptorRenderer.Render(s);
        var hits = PhysicalVocab.Count(w => rendered.Contains(w, StringComparison.OrdinalIgnoreCase));

        hits.Should().BeGreaterOrEqualTo(5,
            "the body renderer must lean on the physical/temporal vocabulary that gives the axis its distinctness");
    }

    [Fact]
    public void RenderParenthetical_FormatsAsSingleLine()
    {
        var s = NeutralState();
        s.Tiredness = 0.75f;
        s.Restlessness = 0.75f;

        var rendered = InteroceptiveDescriptorRenderer.RenderParenthetical(s);

        rendered.Should().StartWith("(Your body right now:");
        rendered.Should().EndWith(".)");
        rendered.Should().NotContain("\n");
    }

    [Fact]
    public void Render_VoiceMode_UsesSlightlyDifferentPhrasingButSameHeaderShape()
    {
        var s = new EmotionalState { Restlessness = 0.85f };

        var rendered = InteroceptiveDescriptorRenderer.Render(s, isVoice: true);

        rendered.Should().Contain("YOUR CURRENT BODY-SENSE");
        rendered.Should().Contain("restless");
        rendered.Should().NotContainAny(ForbiddenWarmVocab);
    }
}
